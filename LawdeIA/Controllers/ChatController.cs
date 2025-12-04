using LawdeIA.Data;
using LawdeIA.Data;
using LawdeIA.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace LawdeIA.Controllers
{
    [Authorize]
    public class ChatController : Controller
    {
        private readonly LawdeIAContext _context;
        private readonly ILogger<ChatController> _logger;
        private readonly HttpClient _httpClient;

        // CONFIGURACIÓN
        private const string GEMINI_API_KEY = "AIzaSyAWFhRSrUJJRQpMGLhhqPwaIPVqry6esCg";
        private const string GEMINI_CHAT_MODEL = "gemini-2.0-flash";
        private const string GEMINI_EMBEDDING_MODEL = "text-embedding-004";

        public ChatController(LawdeIAContext context, ILogger<ChatController> logger)
        {
            _context = context;
            _logger = logger;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        }

        public IActionResult Index() => View();

        // --- ENDPOINTS BÁSICOS ---
        [HttpGet]
        public async Task<IActionResult> GetConversations()
        {
            var userId = GetUserId();
            var list = await _context.Conversations
                .Where(c => c.UserID == userId && c.Status == "Active")
                .OrderByDescending(c => c.LastUpdated)
                .Select(c => new {
                    id = c.ConversationID,
                    title = c.Title,
                    timestamp = c.LastUpdated.ToString("dd/MM HH:mm"),
                    messageCount = c.MessageCount,
                    lastMessagePreview = c.LastMessagePreview,
                    isPinned = c.IsPinned,
                    selectedDocumentId = c.SelectedDocumentID
                })
                .ToListAsync();
            return Json(list);
        }

        [HttpGet]
        public async Task<IActionResult> LoadConversation(int conversationId)
        {
            var userId = GetUserId();
            var conv = await _context.Conversations
                .Include(c => c.Messages)
                .Include(c => c.SelectedDocument)
                .FirstOrDefaultAsync(c => c.ConversationID == conversationId && c.UserID == userId);

            if (conv == null) return Json(new { success = false });

            return Json(new
            {
                success = true,
                title = conv.Title,
                selectedDocumentId = conv.SelectedDocumentID,
                selectedDocumentName = conv.SelectedDocument != null ? conv.SelectedDocument.Title : null,
                messages = conv.Messages.OrderBy(m => m.CreatedAt).Select(m => new {
                    content = m.Content,
                    senderType = m.SenderType,
                    timestamp = m.CreatedAt.ToString("HH:mm"),
                    isEdited = m.IsEdited
                })
            });
        }

        [HttpPost]
        public async Task<IActionResult> DeleteConversation([FromBody] DeleteRequest req)
        {
            var userId = GetUserId();
            var conv = await _context.Conversations
                .FirstOrDefaultAsync(c => c.ConversationID == req.ConversationId && c.UserID == userId);

            if (conv != null)
            {
                conv.Status = "Deleted";
                await _context.SaveChangesAsync();

                // Registrar en auditoría
                await LogAudit(userId, "ConversationDeleted", "Conversation", req.ConversationId,
                    $"Conversación '{conv.Title}' eliminada");

                return Json(new { success = true });
            }

            return Json(new { success = false });
        }

        [HttpPost]
        public async Task<IActionResult> PinConversation([FromBody] PinConversationRequest req)
        {
            var userId = GetUserId();
            var conv = await _context.Conversations
                .FirstOrDefaultAsync(c => c.ConversationID == req.ConversationId && c.UserID == userId);

            if (conv != null)
            {
                conv.IsPinned = req.IsPinned;
                conv.LastUpdated = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                return Json(new { success = true });
            }

            return Json(new { success = false });
        }

        [HttpPost]
        public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest req)
        {
            try
            {
                var userId = GetUserId();
                Conversation conv = null;
                int? effectiveDocumentId = req.SelectedDocumentId;

                if (req.ConversationId <= 0)
                {
                    // Nueva conversación
                    conv = new Conversation
                    {
                        UserID = userId,
                        Title = "Nueva conversación",
                        CreatedAt = DateTime.UtcNow,
                        LastUpdated = DateTime.UtcNow,
                        Status = "Active",
                        SelectedDocumentID = effectiveDocumentId,
                        MessageCount = 0
                    };
                    _context.Conversations.Add(conv);
                    await _context.SaveChangesAsync();
                    req.ConversationId = conv.ConversationID;

                    await LogAudit(userId, "ConversationCreated", "Conversation", conv.ConversationID,
                        "Nueva conversación creada");
                }
                else
                {
                    // Conversación existente
                    conv = await _context.Conversations
                        .Include(c => c.SelectedDocument)
                        .FirstOrDefaultAsync(c => c.ConversationID == req.ConversationId && c.UserID == userId);

                    if (conv == null) return Json(new { success = false, error = "Conversación no encontrada" });

                    // Actualizar documento seleccionado si se envía uno nuevo
                    if (req.SelectedDocumentId.HasValue && req.SelectedDocumentId != conv.SelectedDocumentID)
                    {
                        conv.SelectedDocumentID = req.SelectedDocumentId;
                        effectiveDocumentId = req.SelectedDocumentId;
                    }
                    else
                    {
                        effectiveDocumentId = conv.SelectedDocumentID;
                    }
                }

                // Crear mensaje del usuario
                var userMessage = new Message
                {
                    ConversationID = req.ConversationId,
                    SenderType = "User",
                    Content = req.Message,
                    CreatedAt = DateTime.UtcNow,
                    ContentHash = ComputeHash(req.Message),
                    TokensUsed = EstimateTokens(req.Message)
                };
                _context.Messages.Add(userMessage);
                await _context.SaveChangesAsync();

                // Actualizar contador de mensajes y última actividad
                conv.MessageCount++;
                conv.LastUpdated = DateTime.UtcNow;
                conv.LastMessagePreview = req.Message.Length > 100 ? req.Message.Substring(0, 100) + "..." : req.Message;

                string context = "";
                string documentTitle = "";
                bool hasDocumentContext = false;
                RAGDocument selectedDocument = null;

                try
                {
                    // Buscar en documentos si hay uno seleccionado
                    if (effectiveDocumentId.HasValue && effectiveDocumentId > 0)
                    {
                        selectedDocument = await _context.RAGDocuments
                            .Include(d => d.Chunks)
                            .FirstOrDefaultAsync(d => d.DocumentID == effectiveDocumentId &&
                                                    d.UserID == userId && // Solo documentos del usuario
                                                    d.Status == "Active" &&
                                                    d.AccessLevel == "Private"); // Solo documentos privados

                        if (selectedDocument != null)
                        {
                            documentTitle = selectedDocument.Title;

                            // Actualizar último acceso del documento
                            selectedDocument.LastAccessed = DateTime.UtcNow;

                            _logger.LogInformation($"🔍 Buscando en documento privado: {selectedDocument.Title}");

                            // PRIMERO: Intentar búsqueda en cache
                            var cachedResults = await GetCachedSearch(req.Message, selectedDocument.DocumentID);
                            if (cachedResults != null)
                            {
                                context = cachedResults;
                                hasDocumentContext = !string.IsNullOrEmpty(context);
                                _logger.LogInformation($"📦 Resultado desde cache: {hasDocumentContext}");
                            }

                            // SEGUNDO: Búsqueda vectorial si no hay cache
                            if (!hasDocumentContext)
                            {
                                var vector = await GetEmbedding(req.Message);
                                if (vector != null)
                                {
                                    context = await SearchVectorInDocument(vector, selectedDocument.DocumentID);
                                    hasDocumentContext = !string.IsNullOrEmpty(context);
                                    _logger.LogInformation($"📊 Resultado búsqueda vectorial: {hasDocumentContext}");

                                    // Guardar en cache
                                    if (hasDocumentContext)
                                    {
                                        await CacheSearch(req.Message, selectedDocument.DocumentID, context);
                                    }
                                }
                            }

                            // TERCERO: Usar contenido completo como fallback
                            if (!hasDocumentContext)
                            {
                                _logger.LogInformation($"🔄 Usando contenido completo del documento");
                                context = selectedDocument.Content.Length > 6000
                                    ? selectedDocument.Content.Substring(0, 6000) + "..."
                                    : selectedDocument.Content;
                                hasDocumentContext = true;
                            }
                        }
                        else
                        {
                            _logger.LogWarning($"❌ Documento privado no encontrado: {effectiveDocumentId} para usuario: {userId}");
                        }
                    }

                    // SIEMPRE buscar en la base de conocimiento global (documentos públicos de admin)
                    // Esto sucede tanto si hay documento seleccionado como si no
                    string globalKnowledgeContext = await GetGlobalKnowledgeContext(req.Message, userId);

                    if (!string.IsNullOrEmpty(globalKnowledgeContext))
                    {
                        // Combinar contexto del documento seleccionado (si existe) con el contexto global
                        if (hasDocumentContext)
                        {
                            context = $"{context}\n\n--- BASE DE CONOCIMIENTO GLOBAL ---\n{globalKnowledgeContext}";
                        }
                        else
                        {
                            context = globalKnowledgeContext;
                            hasDocumentContext = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Error en búsqueda vectorial");
                }

                // Generar respuesta de IA
                var (aiText, tokensUsed) = await AskGemini(req.Message, context, documentTitle, hasDocumentContext, effectiveDocumentId.HasValue);

                // Crear mensaje de la IA
                var aiMessage = new Message
                {
                    ConversationID = req.ConversationId,
                    SenderType = "AI",
                    Content = aiText,
                    CreatedAt = DateTime.UtcNow,
                    ContentHash = ComputeHash(aiText),
                    TokensUsed = tokensUsed,
                    ModelUsed = GEMINI_CHAT_MODEL
                };
                _context.Messages.Add(aiMessage);

                // Actualizar contadores
                conv.MessageCount++;
                conv.LastMessagePreview = aiText.Length > 100 ? aiText.Substring(0, 100) + "..." : aiText;

                // Actualizar título de la conversación si es nueva
                if (conv.Title == "Nueva conversación" || string.IsNullOrEmpty(conv.Title))
                {
                    conv.Title = GenerateConversationTitle(aiText, req.Message);
                }

                // Registrar métricas de uso
                await LogUsage(userId, "chat", tokensUsed + (userMessage.TokensUsed ?? 0));

                await _context.SaveChangesAsync();

                return Json(new
                {
                    success = true,
                    conversationId = req.ConversationId,
                    aiMessage = new
                    {
                        content = aiText,
                        senderType = "AI",
                        timestamp = DateTime.Now.ToString("HH:mm")
                    },
                    selectedDocumentId = effectiveDocumentId,
                    selectedDocumentName = selectedDocument?.Title
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error en SendMessage");
                return Json(new { success = false, error = ex.Message });
            }
        }

        // --- GESTIÓN DE DOCUMENTOS ---
        [HttpGet]
        public async Task<IActionResult> GetUserDocuments()
        {
            var userId = GetUserId();

            // SOLO obtener documentos privados del usuario (NO incluir documentos públicos de admin)
            var documents = await _context.RAGDocuments
                .Where(d => d.Status == "Active" &&
                           d.UserID == userId && // Solo documentos del usuario
                           d.AccessLevel == "Private") // Solo documentos privados
                .OrderByDescending(d => d.LastAccessed ?? d.CreatedAt)
                .Select(d => new {
                    id = d.DocumentID,
                    title = d.Title,
                    fileName = d.FileName,
                    createdAt = d.CreatedAt.ToString("dd/MM/yyyy"),
                    lastAccessed = d.LastAccessed.HasValue ? d.LastAccessed.Value.ToString("dd/MM/yyyy") : null,
                    source = d.Source,
                    fileSize = d.FileSize,
                    fileType = d.FileType,
                    chunkCount = d.ChunkCount,
                    contentLength = d.Content.Length,
                    processingStatus = d.ProcessingStatus,
                    accessLevel = d.AccessLevel,
                    isOwner = true,
                    ownerName = "Tú"
                })
                .ToListAsync();

            return Json(documents);
        }

        [HttpGet]
        public async Task<IActionResult> GetDocumentInfo(int documentId)
        {
            var userId = GetUserId();

            // Solo documentos privados del usuario
            var document = await _context.RAGDocuments
                .Where(d => d.DocumentID == documentId &&
                           d.Status == "Active" &&
                           d.UserID == userId && // Solo documentos del usuario
                           d.AccessLevel == "Private") // Solo documentos privados
                .Select(d => new {
                    id = d.DocumentID,
                    title = d.Title,
                    fileName = d.FileName,
                    createdAt = d.CreatedAt.ToString("dd/MM/yyyy HH:mm"),
                    fileSize = d.FileSize,
                    fileType = d.FileType,
                    chunkCount = d.ChunkCount,
                    contentLength = d.Content.Length,
                    contentSummary = d.ContentSummary,
                    processingStatus = d.ProcessingStatus,
                    embeddingModel = d.EmbeddingModel,
                    accessLevel = d.AccessLevel,
                    isOwner = true,
                    ownerName = "Tú"
                })
                .FirstOrDefaultAsync();

            if (document == null) return Json(new { success = false, error = "Documento no encontrado" });

            return Json(new { success = true, document });
        }

        [HttpPost]
        [RequestSizeLimit(100_000_000)] // 100MB
        public async Task<IActionResult> UploadDocument(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return Json(new { success = false, error = "Archivo vacío" });

            try
            {
                var userId = GetUserId();
                string textContent = "";
                string ext = Path.GetExtension(file.FileName).ToLower();

                _logger.LogInformation($"📤 Iniciando upload de documento: {file.FileName}");

                // Obtener si el usuario actual es admin
                var isAdmin = await _context.Users
                    .Where(u => u.UserID == userId)
                    .Select(u => u.Role == "Admin")
                    .FirstOrDefaultAsync();

                // Crear documento con estado "Processing"
                var doc = new RAGDocument
                {
                    UserID = userId,
                    Title = file.FileName.Length > 100 ? file.FileName.Substring(0, 100) : file.FileName,
                    FileName = file.FileName,
                    FileSize = file.Length,
                    FileType = ext.TrimStart('.'),
                    Source = "Upload",
                    CreatedAt = DateTime.UtcNow,
                    LastUpdated = DateTime.UtcNow,
                    // IMPORTANTE: Admin sube documentos públicos (base de conocimiento)
                    // Usuarios normales suben documentos privados
                    AccessLevel = isAdmin ? "Public" : "Private",
                    Status = "Processing",
                    ProcessingStatus = "ExtractingText"
                };
                _context.RAGDocuments.Add(doc);
                await _context.SaveChangesAsync();

                await LogAudit(userId, "DocumentUploaded", "RAGDocument", doc.DocumentID,
                    $"Documento '{file.FileName}' subido" + (isAdmin ? " (Base de conocimiento global)" : " (Documento privado)"));

                // 1. EXTRACCIÓN DE TEXTO
                if (ext == ".pdf")
                {
                    try
                    {
                        _logger.LogInformation($"🔧 Extrayendo texto de PDF...");
                        using (var stream = file.OpenReadStream())
                        using (var pdf = PdfDocument.Open(stream))
                        {
                            var sb = new StringBuilder();
                            foreach (var page in pdf.GetPages())
                            {
                                var text = ContentOrderTextExtractor.GetText(page);
                                sb.Append(text + " ");
                            }
                            textContent = sb.ToString().Trim();
                        }
                        doc.ProcessingStatus = "TextExtracted";
                    }
                    catch (Exception pdfEx)
                    {
                        _logger.LogError(pdfEx, "❌ Error en extracción PDF local");
                        doc.ProcessingStatus = "ExtractionFailed";
                        await _context.SaveChangesAsync();
                        return Json(new { success = false, error = "Error extrayendo texto del PDF" });
                    }
                }
                else if (ext == ".txt" || ext == ".md")
                {
                    using (var reader = new StreamReader(file.OpenReadStream()))
                    {
                        textContent = await reader.ReadToEndAsync();
                        doc.ProcessingStatus = "TextExtracted";
                    }
                }

                // 2. OCR CON GEMINI PARA IMÁGENES/ESCANEOS
                if (string.IsNullOrWhiteSpace(textContent))
                {
                    if (ext == ".pdf" || ext == ".jpg" || ext == ".png" || ext == ".jpeg")
                    {
                        _logger.LogInformation($"👁️ Usando OCR de Gemini...");
                        doc.ProcessingStatus = "UsingOCR";
                        await _context.SaveChangesAsync();

                        textContent = await ExtractTextWithGemini(file);
                        if (!string.IsNullOrWhiteSpace(textContent))
                        {
                            doc.ProcessingStatus = "OCRExtracted";
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(textContent))
                {
                    doc.Status = "Error";
                    doc.ProcessingStatus = "NoContentExtracted";
                    await _context.SaveChangesAsync();
                    return Json(new { success = false, error = "No se pudo extraer contenido del archivo" });
                }

                // 3. ACTUALIZAR DOCUMENTO CON CONTENIDO
                doc.Content = textContent;
                doc.ContentSummary = GenerateContentSummary(textContent);
                doc.ProcessingStatus = "GeneratingEmbeddings";
                doc.LastUpdated = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                // 4. GENERAR CHUNKS Y EMBEDDINGS
                _logger.LogInformation($"🧠 Generando embeddings...");
                var chunksCreated = await GenerateDocumentEmbeddings(doc.DocumentID, textContent);

                doc.ChunkCount = chunksCreated;
                doc.EmbeddingModel = GEMINI_EMBEDDING_MODEL;
                doc.Status = "Active";
                doc.ProcessingStatus = "Completed";
                doc.LastUpdated = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                _logger.LogInformation($"✅ Documento procesado: {doc.Title} - {chunksCreated} chunks" +
                                      (isAdmin ? " (Base de conocimiento global)" : " (Documento privado)"));

                await LogAudit(userId, "DocumentProcessed", "RAGDocument", doc.DocumentID,
                    $"Documento procesado con {chunksCreated} chunks" + (isAdmin ? " - Base de conocimiento" : " - Documento privado"));

                return Json(new
                {
                    success = true,
                    document = new
                    {
                        id = doc.DocumentID,
                        title = doc.Title,
                        chunkCount = doc.ChunkCount,
                        accessLevel = doc.AccessLevel,
                        isOwner = true
                    },
                    isBaseKnowledge = isAdmin // Para que la UI sepa si mostrar o no
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error en UploadDocument");
                return Json(new { success = false, error = "Error procesando documento: " + ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> DeleteDocument([FromBody] DeleteDocumentRequest req)
        {
            try
            {
                var userId = GetUserId();
                var document = await _context.RAGDocuments
                    .FirstOrDefaultAsync(d => d.DocumentID == req.DocumentId && d.UserID == userId);

                if (document == null)
                    return Json(new { success = false, error = "Documento no encontrado" });

                // Verificar si el documento está siendo usado en conversaciones activas
                var activeConversations = await _context.Conversations
                    .Where(c => c.SelectedDocumentID == req.DocumentId && c.Status == "Active")
                    .CountAsync();

                if (activeConversations > 0)
                {
                    return Json(new
                    {
                        success = false,
                        error = $"No se puede eliminar el documento. Está siendo usado en {activeConversations} conversación(es) activa(s)."
                    });
                }

                // IMPORTANTE: Si el documento es público (base de conocimiento), solo admin puede eliminarlo
                if (document.AccessLevel == "Public")
                {
                    var isAdmin = await _context.Users
                        .Where(u => u.UserID == userId)
                        .Select(u => u.Role == "Admin")
                        .FirstOrDefaultAsync();

                    if (!isAdmin)
                    {
                        return Json(new { success = false, error = "Solo administradores pueden eliminar documentos de la base de conocimiento" });
                    }
                }

                // Eliminar chunks y embeddings (CASCADE en BD)
                document.Status = "Deleted";
                await _context.SaveChangesAsync();

                _logger.LogInformation($"✅ Documento {req.DocumentId} marcado como eliminado");

                await LogAudit(userId, "DocumentDeleted", "RAGDocument", req.DocumentId,
                    $"Documento '{document.Title}' eliminado" + (document.AccessLevel == "Public" ? " (Base de conocimiento)" : ""));

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error en DeleteDocument");
                return Json(new { success = false, error = ex.Message });
            }
        }

        // --- NUEVO MÉTODO: Obtener contexto de base de conocimiento global ---
        private async Task<string> GetGlobalKnowledgeContext(string query, int userId)
        {
            try
            {
                var vector = await GetEmbedding(query);
                if (vector == null) return "";

                // Buscar SOLO en documentos públicos (base de conocimiento)
                var embeddings = await _context.RAGEmbeddings
                    .Include(e => e.Chunk)
                    .Include(e => e.Document)
                    .Where(e => e.Document.Status == "Active" &&
                               e.Document.AccessLevel == "Public" && // Solo documentos públicos
                               e.IsActive &&
                               e.Document.User.Role == "Admin") // Solo documentos de admin
                    .Select(e => new {
                        e.Vector,
                        e.Chunk.Content,
                        DocumentTitle = e.Document.Title,
                        DocumentOwner = e.Document.User.Username
                    })
                    .ToListAsync();

                var results = new List<(string Content, double Score, string DocumentTitle)>();
                foreach (var emb in embeddings)
                {
                    if (emb.Vector == null || emb.Vector.Length == 0) continue;

                    var dbVec = BytesToVector(emb.Vector);
                    double similarity = CalculateCosineSimilarity(vector, dbVec);
                    if (similarity > 0.45) // Umbral de relevancia
                        results.Add((emb.Content, similarity, emb.DocumentTitle));
                }

                if (!results.Any()) return "";

                // Tomar los mejores 3 resultados
                var topResults = results.OrderByDescending(x => x.Score).Take(3).ToList();

                // Formatear contexto
                var contextBuilder = new StringBuilder();
                contextBuilder.AppendLine("**Base de Conocimiento Global:**");

                foreach (var result in topResults)
                {
                    contextBuilder.AppendLine($"📄 **{result.DocumentTitle}**");
                    contextBuilder.AppendLine($"{result.Content}");
                    contextBuilder.AppendLine();
                }

                _logger.LogInformation($"✅ Contexto global encontrado: {topResults.Count} fragmentos relevantes");

                return contextBuilder.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error al obtener contexto de base de conocimiento");
                return "";
            }
        }

        // --- HELPERS AVANZADOS ---
        private int GetUserId() => int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out int id) ? id : 0;

        private async Task<int> GenerateDocumentEmbeddings(int documentId, string textContent)
        {
            var chunks = SplitTextIntoChunks(textContent);
            int chunksCreated = 0;

            _logger.LogInformation($"✂️ Dividiendo texto en {chunks.Count} chunks...");

            // Eliminar chunks existentes (por si hay reprocesamiento)
            var existingChunks = await _context.RAGDocumentChunks
                .Where(c => c.DocumentID == documentId)
                .ToListAsync();
            _context.RAGDocumentChunks.RemoveRange(existingChunks);

            // Crear nuevos chunks
            for (int i = 0; i < chunks.Count && i < 100; i++) // Límite de 100 chunks por documento
            {
                var chunk = chunks[i];
                if (string.IsNullOrWhiteSpace(chunk) || chunk.Length < 20) continue;

                var documentChunk = new RAGDocumentChunk
                {
                    DocumentID = documentId,
                    ChunkIndex = i,
                    Content = chunk,
                    ContentHash = ComputeHash(chunk),
                    TokenCount = EstimateTokens(chunk),
                    CreatedAt = DateTime.UtcNow
                };
                _context.RAGDocumentChunks.Add(documentChunk);
                await _context.SaveChangesAsync(); // Guardar para obtener ChunkID

                // Generar embedding para el chunk
                var vector = await GetEmbedding(chunk);
                if (vector != null && vector.Length > 0)
                {
                    var embedding = new RAGEmbedding
                    {
                        ChunkID = documentChunk.ChunkID,
                        DocumentID = documentId,
                        Vector = VectorToBytes(vector),
                        VectorDimensions = (short)vector.Length,
                        ChunkIndex = i,
                        ModelUsed = GEMINI_EMBEDDING_MODEL,
                        CreatedAt = DateTime.UtcNow,
                        IsActive = true
                    };
                    _context.RAGEmbeddings.Add(embedding);
                    chunksCreated++;
                }

                // Guardar cada 5 chunks
                if (i % 5 == 0)
                {
                    await _context.SaveChangesAsync();
                }
            }

            await _context.SaveChangesAsync();
            _logger.LogInformation($"🎉 Embeddings completados: {chunksCreated} chunks procesados");

            return chunksCreated;
        }

        private List<string> SplitTextIntoChunks(string text, int maxChunkSize = 800)
        {
            var chunks = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return chunks;

            // Dividir por párrafos
            var paragraphs = text.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var paragraph in paragraphs)
            {
                var trimmed = paragraph.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                if (trimmed.Length <= maxChunkSize)
                {
                    chunks.Add(trimmed);
                }
                else
                {
                    // Dividir párrafo largo
                    var sentences = trimmed.Split(new[] { '.', '!', '?', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    var currentChunk = new StringBuilder();

                    foreach (var sentence in sentences)
                    {
                        var trimmedSentence = sentence.Trim();
                        if (string.IsNullOrEmpty(trimmedSentence)) continue;

                        if (currentChunk.Length + trimmedSentence.Length > maxChunkSize && currentChunk.Length > 0)
                        {
                            chunks.Add(currentChunk.ToString());
                            currentChunk.Clear();
                        }

                        if (currentChunk.Length > 0)
                            currentChunk.Append(". ");
                        currentChunk.Append(trimmedSentence);
                    }

                    if (currentChunk.Length > 0)
                        chunks.Add(currentChunk.ToString());
                }
            }

            return chunks.Where(c => !string.IsNullOrWhiteSpace(c) && c.Length > 10).ToList();
        }

        private async Task<float[]> GetEmbedding(string text)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return null;

                var cleanText = text.Replace("\n", " ").Substring(0, Math.Min(text.Length, 10000));

                var url = $"https://generativelanguage.googleapis.com/v1beta/models/{GEMINI_EMBEDDING_MODEL}:embedContent?key={GEMINI_API_KEY}";
                var body = new
                {
                    model = $"models/{GEMINI_EMBEDDING_MODEL}",
                    content = new
                    {
                        parts = new[] {
                            new {
                                text = cleanText
                            }
                        }
                    }
                };

                var jsonContent = JsonSerializer.Serialize(body);
                var response = await _httpClient.PostAsync(url,
                    new StringContent(jsonContent, Encoding.UTF8, "application/json"));

                if (!response.IsSuccessStatusCode) return null;

                var responseContent = await response.Content.ReadAsStringAsync();
                var data = JsonSerializer.Deserialize<GeminiEmbeddingResponse>(
                    responseContent,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

                return data?.Embedding?.Values;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error obteniendo embedding");
                return null;
            }
        }

        private async Task<string> SearchVector(float[] queryVector, int userId)
        {
            if (queryVector == null || queryVector.Length == 0) return "";

            var embeddings = await _context.RAGEmbeddings
                .Include(e => e.Chunk)
                .Where(e => e.Document.UserID == userId &&
                           e.Document.Status == "Active" &&
                           e.Document.AccessLevel == "Private" && // Solo documentos privados
                           e.IsActive)
                .Select(e => new { e.Vector, e.Chunk.Content, DocumentId = e.DocumentID })
                .ToListAsync();

            var results = new List<(string Content, double Score)>();
            foreach (var emb in embeddings)
            {
                if (emb.Vector == null || emb.Vector.Length == 0) continue;

                var dbVec = BytesToVector(emb.Vector);
                double similarity = CalculateCosineSimilarity(queryVector, dbVec);
                if (similarity > 0.45)
                    results.Add((emb.Content, similarity));
            }

            return results.Any() ?
                string.Join("\n\n", results.OrderByDescending(x => x.Score).Take(3).Select(x => x.Content)) : "";
        }

        private async Task<string> SearchVectorInDocument(float[] queryVector, int documentId)
        {
            if (queryVector == null || queryVector.Length == 0) return "";

            var embeddings = await _context.RAGEmbeddings
                .Include(e => e.Chunk)
                .Where(e => e.DocumentID == documentId && e.IsActive)
                .Select(e => new { e.Vector, e.Chunk.Content, e.ChunkIndex })
                .ToListAsync();

            var results = new List<(string Content, double Score, int Index)>();
            foreach (var emb in embeddings)
            {
                if (emb.Vector == null || emb.Vector.Length == 0) continue;

                var dbVec = BytesToVector(emb.Vector);
                double similarity = CalculateCosineSimilarity(queryVector, dbVec);
                if (similarity > 0.45)
                    results.Add((emb.Content, similarity, emb.ChunkIndex));
            }

            return results.Any() ?
                string.Join("\n\n", results.OrderByDescending(x => x.Score).Take(3).Select(x => x.Content)) : "";
        }

        private async Task<string?> GetCachedSearch(string query, int documentId)
        {
            var queryHash = ComputeHash(query);
            var cache = await _context.RAGSearchCaches
                .FirstOrDefaultAsync(c => c.QueryHash.SequenceEqual(queryHash) &&
                                        c.DocumentID == documentId &&
                                        c.ExpiresAt > DateTime.UtcNow);

            if (cache != null)
            {
                cache.ExpiresAt = DateTime.UtcNow.AddMinutes(60); // Extender TTL
                await _context.SaveChangesAsync();
                return cache.Results;
            }

            return null;
        }

        private async Task CacheSearch(string query, int documentId, string results)
        {
            var cache = new RAGSearchCache
            {
                QueryHash = ComputeHash(query),
                DocumentID = documentId,
                QueryText = query.Length > 1000 ? query.Substring(0, 1000) : query,
                Results = results,
                ResultCount = results.Split("\n\n").Length,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(60) // 1 hora de cache
            };

            _context.RAGSearchCaches.Add(cache);
            await _context.SaveChangesAsync();
        }

        private double CalculateCosineSimilarity(float[] vector1, float[] vector2)
        {
            if (vector1 == null || vector2 == null || vector1.Length != vector2.Length) return 0;

            double dot = 0, mag1 = 0, mag2 = 0;
            for (int i = 0; i < vector1.Length; i++)
            {
                dot += vector1[i] * vector2[i];
                mag1 += vector1[i] * vector1[i];
                mag2 += vector2[i] * vector2[i];
            }

            mag1 = Math.Sqrt(mag1);
            mag2 = Math.Sqrt(mag2);

            return (mag1 * mag2) == 0 ? 0 : dot / (mag1 * mag2);
        }

        private async Task<(string response, int tokensUsed)> AskGemini(string msg, string ctx, string documentTitle, bool hasDocumentContext, bool hasSelectedDocument)
        {
            var prompt = BuildPrompt(msg, ctx, documentTitle, hasDocumentContext, hasSelectedDocument);

            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{GEMINI_CHAT_MODEL}:generateContent?key={GEMINI_API_KEY}";
            var body = new
            {
                contents = new[] {
                    new {
                        parts = new[] {
                            new {
                                text = prompt
                            }
                        }
                    }
                },
                generationConfig = new
                {
                    temperature = hasDocumentContext ? 0.1 : 0.3,
                    topK = 40,
                    topP = 0.8,
                    maxOutputTokens = 4096
                }
            };

            try
            {
                var jsonContent = JsonSerializer.Serialize(body);
                var response = await _httpClient.PostAsync(url,
                    new StringContent(jsonContent, Encoding.UTF8, "application/json"));

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError($"Error Gemini API: {response.StatusCode} - {errorContent}");
                    return ("Lo siento, hubo un problema técnico. ¿Podríamos intentarlo de nuevo?", 0);
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<GeminiResponse>(
                    responseJson,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

                var responseText = result?.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "No pude generar una respuesta.";

                // Estimar tokens usados (aproximadamente 4 caracteres por token)
                var tokensUsed = (int)Math.Ceiling(responseText.Length / 4.0);

                return (responseText, tokensUsed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en AskGemini");
                return ("Hubo un error de conexión. Por favor, intenta nuevamente.", 0);
            }
        }

        private string BuildPrompt(string msg, string ctx, string documentTitle, bool hasDocumentContext, bool hasSelectedDocument)
        {
            if (hasDocumentContext && hasSelectedDocument && ctx.Contains("--- BASE DE CONOCIMIENTO GLOBAL ---"))
            {
                return $@"Eres LawdeIA, un asistente legal especializado.

El usuario está analizando el documento: **{documentTitle}**

**INFORMACIÓN DEL DOCUMENTO SELECCIONADO:**
{ctx}

**CONSULTA DEL USUARIO:** {msg}

Como asistente legal, responde de manera PROFESIONAL basándote en:
1. El documento seleccionado por el usuario (información específica)
2. La base de conocimiento global (información general de referencia)

Proporciona una respuesta integrada que combine ambas fuentes cuando sea relevante.";
            }
            else if (hasDocumentContext && hasSelectedDocument)
            {
                return $@"Eres LawdeIA, un asistente legal especializado.

Estoy analizando el documento: **{documentTitle}**

**CONTEXTO RELEVANTE DEL DOCUMENTO:**
{ctx}

**CONSULTA DEL USUARIO:** {msg}

Como asistente legal, responde de manera PROFESIONAL basándote EXCLUSIVAMENTE en el documento proporcionado. Proporciona una respuesta detallada y precisa, citando información específica del documento cuando sea relevante.";
            }
            else if (ctx.Contains("Base de Conocimiento Global"))
            {
                // Solo contexto de base de conocimiento (sin documento seleccionado)
                return $@"Eres LawdeIA, un asistente legal especializado con acceso a base de conocimiento global.

**INFORMACIÓN DE LA BASE DE CONOCIMIENTO:**
{ctx}

**CONSULTA DEL USUARIO:** {msg}

Proporciona una respuesta profesional basándote en la base de conocimiento disponible. 
Si la consulta es muy específica y no encuentras información relevante, sugiere al usuario que suba un documento relacionado para un análisis más preciso.";
            }
            else if (IsGreeting(msg))
            {
                return $@"Eres LawdeIA, un asistente legal especializado con acceso a base de conocimiento global.

**El usuario dice:** {msg}

Responde al saludo presentándote como LawdeIA, asistente legal con acceso a base de conocimiento. Sé amable pero conciso.";
            }
            else if (IsCapabilitiesQuery(msg))
            {
                return $@"Eres LawdeIA, un asistente legal especializado.

**CONSULTA DEL USUARIO:** {msg}

Explica claramente tus capacidades:
1. Acceso a base de conocimiento global (documentos de referencia disponibles para todos)
2. Análisis de documentos personales (el usuario puede subir sus propios documentos)
3. Respuestas legales generales basadas en conocimiento experto

Sé específico pero conciso.";
            }
            else
            {
                return $@"Eres LawdeIA, asistente legal especializado con acceso a base de conocimiento global.

**CONSULTA DEL USUARIO:** {msg}

Proporciona ayuda legal general basada en tu conocimiento experto. 
Si la consulta requiere información más específica, sugiere al usuario que suba documentos relevantes para un análisis detallado.";
            }
        }

        // --- UTILITARIOS ---
        private byte[] ComputeHash(string text)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            return sha256.ComputeHash(Encoding.UTF8.GetBytes(text));
        }

        private byte[] VectorToBytes(float[] vector)
        {
            var byteArray = new byte[vector.Length * 4];
            Buffer.BlockCopy(vector, 0, byteArray, 0, byteArray.Length);
            return byteArray;
        }

        private float[] BytesToVector(byte[] bytes)
        {
            var vector = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
            return vector;
        }

        private int EstimateTokens(string text)
        {
            // Estimación aproximada: 1 token ≈ 4 caracteres
            return (int)Math.Ceiling(text.Length / 4.0);
        }

        private string GenerateContentSummary(string content)
        {
            if (content.Length <= 200) return content;
            return content.Substring(0, 200) + "...";
        }

        private string GenerateConversationTitle(string aiResponse, string userMessage)
        {
            var titleSource = aiResponse.Length > 50 ? aiResponse : userMessage;
            var title = titleSource.Length > 50 ? titleSource.Substring(0, 50) + "..." : titleSource;
            return title.Replace("\n", " ").Replace("\r", "");
        }

        private async Task LogAudit(int? userId, string action, string entityType, long? entityId, string details)
        {
            var auditLog = new AuditLog
            {
                UserID = userId,
                Action = action,
                EntityType = entityType,
                EntityID = entityId,
                Details = details,
                IPAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = Request.Headers["User-Agent"].ToString(),
                CreatedAt = DateTime.UtcNow
            };
            _context.AuditLogs.Add(auditLog);
            await _context.SaveChangesAsync();
        }

        private async Task LogUsage(int userId, string operationType, int tokensUsed)
        {
            // En una implementación real, buscarías el ModelID de la base de datos
            var usageMetric = new UsageMetric
            {
                UserID = userId,
                ModelID = 1, // ID por defecto, deberías tener una tabla de modelos
                OperationType = operationType,
                TokensUsed = tokensUsed,
                CreatedAt = DateTime.UtcNow
            };
            _context.UsageMetrics.Add(usageMetric);
            await _context.SaveChangesAsync();
        }

        private bool IsGreeting(string message)
        {
            var greetings = new[] { "hola", "hi", "hello", "buenos días", "buenas tardes", "buenas noches", "qué tal", "cómo estás" };
            var cleanMessage = message.ToLower().Trim();
            return cleanMessage.Split(' ').Length <= 3 && greetings.Any(greeting => cleanMessage.Contains(greeting));
        }

        private bool IsCapabilitiesQuery(string message)
        {
            var capabilitiesKeywords = new[] { "qué puedes hacer", "qué sabes hacer", "para qué sirves", "qué haces", "tus funciones" };
            var cleanMessage = message.ToLower().Trim();
            return capabilitiesKeywords.Any(keyword => cleanMessage.Contains(keyword));
        }

        private async Task<string> ExtractTextWithGemini(IFormFile file)
        {
            try
            {
                string base64Data;
                using (var ms = new MemoryStream())
                {
                    await file.CopyToAsync(ms);
                    base64Data = Convert.ToBase64String(ms.ToArray());
                }

                string mimeType = GetMimeType(file.FileName);
                var url = $"https://generativelanguage.googleapis.com/v1beta/models/{GEMINI_CHAT_MODEL}:generateContent?key={GEMINI_API_KEY}";

                var body = new
                {
                    contents = new[]
                    {
                        new
                        {
                            parts = new object[]
                            {
                                new { text = "Extrae y transcribe TODO el texto de este documento de manera precisa y completa." },
                                new { inline_data = new { mime_type = mimeType, data = base64Data } }
                            }
                        }
                    },
                    generationConfig = new { temperature = 0.1, maxOutputTokens = 4096 }
                };

                var response = await _httpClient.PostAsync(url,
                    new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

                if (!response.IsSuccessStatusCode) return null;

                var resultJson = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<GeminiResponse>(
                    resultJson,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

                return result?.Candidates?[0]?.Content?.Parts?[0]?.Text;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en ExtractTextWithGemini");
                return null;
            }
        }

        private string GetMimeType(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLower();
            return ext switch
            {
                ".pdf" => "application/pdf",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                _ => "application/octet-stream"
            };
        }

        // DTOs
        public class SendMessageRequest
        {
            public int ConversationId { get; set; }
            public string Message { get; set; } = string.Empty;
            public int? SelectedDocumentId { get; set; }
        }

        public class DeleteRequest
        {
            public int ConversationId { get; set; }
        }

        public class PinConversationRequest
        {
            public int ConversationId { get; set; }
            public bool IsPinned { get; set; }
        }

        public class DeleteDocumentRequest
        {
            public int DocumentId { get; set; }
        }

        public class GeminiResponse
        {
            public GeminiCandidate[]? Candidates { get; set; }
        }

        public class GeminiCandidate
        {
            public GeminiContent? Content { get; set; }
        }

        public class GeminiContent
        {
            public GeminiPart[]? Parts { get; set; }
        }

        public class GeminiPart
        {
            public string? Text { get; set; }
        }

        public class GeminiEmbeddingResponse
        {
            public GeminiEmbeddingVal? Embedding { get; set; }
        }

        public class GeminiEmbeddingVal
        {
            public float[]? Values { get; set; }
        }
    }
}