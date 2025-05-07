using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Storage.Queues;
using Microsoft.Data.Sqlite;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ai_webjob
{
    internal class Program
    {
        static async Task Main()
        {
            SQLitePCL.Batteries.Init();
            var isDevMode = (Environment.GetEnvironmentVariable("APP_ENV") == "DEV");
            var dbConnectionString = isDevMode
                ? @"Data Source=C:\\sidecar-samples\\AI-Samples\\devShopDNC\\Data\\devshopdb.db"
                : @"Data Source=/home/site/wwwroot/Data/devshopdb.db";

            using var connection = new SqliteConnection(dbConnectionString);
            connection.Open();

            var (productId, reviewId) = await ReadProductIdFromQueueAsync();
            if (productId == 0 || reviewId == 0)
            {
                // The reason will already have been printed inside ReadProductIdFromQueueAsync
                return;
            }

            try
            {
                string latestReview = GetReviewTextById(connection, reviewId);
                if (string.IsNullOrWhiteSpace(latestReview))
                {
                    Console.WriteLine($"[EXIT] Review with ID {reviewId} not found or empty.");
                    return;
                }

                string existingSummary = GetExistingSummary(connection, productId);
                if (existingSummary == null)
                {
                    Console.WriteLine($"[EXIT] Product with ID {productId} not found in master table.");
                    return;
                }

                int reviewCount = GetReviewCount(connection, productId);

                var (summary, sentiment) = await GetSummaryAndSentimentFromLocalSLM(existingSummary, latestReview, reviewCount, $"Product ID: {productId}, Review ID: {reviewId}");

                double avgRating = CalculateAverageRating(connection, productId);

                UpdateProductSummary(connection, productId, reviewCount, avgRating, summary, sentiment);

                Console.WriteLine($"Updated Product {productId}: {reviewCount} reviews, Avg Rating {avgRating:F1}, Sentiment: {sentiment}, Summary: {summary}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing Product {productId}: {ex.Message}");
            }

            Console.WriteLine("Done.");
        }

        static async Task<(int ProductId, int ReviewId)> ReadProductIdFromQueueAsync()
        {
            string queueName = Environment.GetEnvironmentVariable("QUEUE_NAME") ?? "new-product-reviews";
            string storageAccountName = Environment.GetEnvironmentVariable("STORAGE_ACCOUNT_NAME") ?? "randomstorageaccount";
            string mi_client_id = Environment.GetEnvironmentVariable("USER_ASSIGNED_MI_CLIENT_ID") ?? string.Empty;
            string connectionString = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING") ?? string.Empty;


            QueueClient? queueClient = null;

            if (!string.IsNullOrEmpty(mi_client_id))
            {
                // User-Assigned Managed Identity should be added to the app and given access to the Storage Account as Storage Queue Data Contributor.
                // Client ID of the User-Assigned Managed Identity should be passed as an AppSetting named USER_ASSIGNED_MI_CLIENT_ID.
                Console.WriteLine($"Connecting to storage queue {queueName} using User-Assigned Managed Identity");
                queueClient = new QueueClient(
                    new Uri($"https://{storageAccountName}.queue.core.windows.net/{queueName}"),
                    new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(mi_client_id)));
            }
            else if (!string.IsNullOrEmpty(connectionString))
            {
                Console.WriteLine($"Connecting to storage queue {queueName} using Connection string");
                queueClient = new QueueClient(connectionString, queueName);
            }
            else
            {
                // System-Assigned Managed Identity should be enabled for the app and given access to the Storage Account as Storage Queue Data Contributor.
                Console.WriteLine($"Connecting to storage queue {queueName} using System-Assigned Managed Identity");
                queueClient = new QueueClient(
                    new Uri($"https://{storageAccountName}.queue.core.windows.net/{queueName}"),
                    new ManagedIdentityCredential());
            }

            if (queueClient == null)
            {
                Console.WriteLine("[EXIT] Queue client could not be created. Check connection string or managed identity.");
                return (0, 0);
            }

            if (!await queueClient.ExistsAsync())
            {
                Console.WriteLine("[EXIT] Queue does not exist.");
                return (0, 0);
            }

            #region localslmreceivemessagefromqueue
            var messages = (await queueClient.ReceiveMessagesAsync(maxMessages: 1, visibilityTimeout: TimeSpan.FromSeconds(5))).Value;
            if (messages.Length == 0)
            {
                Console.WriteLine("[EXIT] No messages found in queue.");
                return (0, 0);
            }

            string messageText = messages[0].MessageText;
            await queueClient.DeleteMessageAsync(messages[0].MessageId, messages[0].PopReceipt);
            #endregion

            try
            {
                var json = JsonSerializer.Deserialize<JsonElement>(messageText);
                int productId = json.GetProperty("productId").GetInt32();
                int reviewId = json.GetProperty("reviewId").GetInt32();
                return (productId, reviewId);
            }
            catch
            {
                Console.WriteLine("[EXIT] Failed to parse queue message into productId and reviewId.");
                return (0, 0);
            }
        }

        static string GetReviewTextById(SqliteConnection connection, int reviewId)
        {
            var sql = "SELECT review_text FROM DevShop_Product_Reviews WHERE review_id = @ReviewID";
            using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@ReviewID", reviewId);
            return cmd.ExecuteScalar()?.ToString()?.Trim() ?? "";
        }

        static int GetReviewCount(SqliteConnection connection, int productId)
        {
            var sql = "SELECT COUNT(*) FROM DevShop_Product_Reviews WHERE ProductID = @ProductID AND review_text IS NOT NULL AND TRIM(review_text) <> ''";
            using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@ProductID", productId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        static double CalculateAverageRating(SqliteConnection connection, int productId)
        {
            var sql = "SELECT AVG(rating) FROM DevShop_Product_Reviews WHERE ProductID = @ProductID AND rating IS NOT NULL";
            using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@ProductID", productId);
            var result = cmd.ExecuteScalar();
            return result != DBNull.Value ? Convert.ToDouble(result) : 0.0;
        }

        static string GetExistingSummary(SqliteConnection connection, int productId)
        {
            var sql = "SELECT review_summary FROM DevShop_Product_Master WHERE ProductID = @ProductID";
            using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@ProductID", productId);
            return cmd.ExecuteScalar()?.ToString()?.Trim();
        }

        static async Task<(string Summary, string Sentiment)> GetSummaryAndSentimentFromLocalSLM(string previousSummary, string latestReview, int reviewCount, string context)
        {
            Console.WriteLine($"Using Local SLM for summarization");

            #region localslmendpoint
            var endpoint = Environment.GetEnvironmentVariable("SLM_AI_ENDPOINT") ?? "http://localhost:11434/v1/";
            #endregion

            Console.WriteLine($"Using SLM Endpoint: {endpoint}");

            var openAIClient = new OpenAIClient(new ApiKeyCredential(key: "api-key"), options: new OpenAIClientOptions
            {
                Endpoint = new Uri(endpoint),
            });

            #region loclslmopenaichatclient
            ChatClient client = openAIClient.GetChatClient("phi4");

            var messages = new ChatMessage[]
            {
                       ChatMessage.CreateSystemMessage("You are an assistant that summarizes user reviews and analyzes sentiment. When a new review is received, integrate it into the existing summary by giving it a weight proportional to the number of past reviews. Ensure the updated summary is concise and within 80 words"),
                       ChatMessage.CreateUserMessage(
                           $"""
                           Here is the existing summary based on {reviewCount-1} reviews: [{previousSummary}]. Please update it with the following new review, giving it weight of 1/{reviewCount}: [{latestReview}].
                           Please respond in the following format:
                           Summary: <summary>
                           Sentiment: <positive/mixed/negative>
                           """
                       )
            };

            ChatCompletion completion = client.CompleteChat(messages);
            #endregion

            string content = completion.Content[0].Text;

            string summary = "";
            string sentiment = "unknown";

            foreach (var line in content.Split('\n'))
            {
                if (line.StartsWith("Summary:", StringComparison.OrdinalIgnoreCase))
                    summary = line["Summary:".Length..].Trim();
                if (line.StartsWith("Sentiment:", StringComparison.OrdinalIgnoreCase))
                    sentiment = line["Sentiment:".Length..].Trim().ToLowerInvariant();
            }

            return (summary, sentiment);
        }

        static void UpdateProductSummary(SqliteConnection connection, int productId, int reviewCount, double avgRating, string summary, string sentiment)
        {
            var sql = @"
                UPDATE DevShop_Product_Master
                SET review_count = @ReviewCount,
                    average_rating = @AvgRating,
                    review_summary = @Summary,
                    product_sentiment = @Sentiment
                WHERE ProductID = @ProductID";

            using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("@ReviewCount", reviewCount);
            command.Parameters.AddWithValue("@AvgRating", avgRating);
            command.Parameters.AddWithValue("@Summary", summary);
            command.Parameters.AddWithValue("@Sentiment", sentiment);
            command.Parameters.AddWithValue("@ProductID", productId);

            if (command.ExecuteNonQuery() == 0)
            {
                throw new Exception("Update failed. Product may not exist in Product_Master.");
            }
        }
    }
}
