using Azure;
using Azure.AI.OpenAI.Assistants;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureOpenAIAssistant.Models;
using Microsoft.AspNetCore.Mvc;

namespace AzureOpenAIAssistant.Controllers
{
    [ApiController]
    [Route("gpt")]
    public class Controller : ControllerBase
    {
        const string _url = "azure-openai-url";
        const string _key = "azure-openai-url-secret";
        const string _blobKey = "azure-blob-conenction-string";

        [HttpPost("assistant")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> PostAsync([FromForm] AssistantRequest request)
        {
            AssistantsClient client = new AssistantsClient(new Uri(_url), new AzureKeyCredential(_key));

            return Ok(await CreateAndRunAssistant(client, request));
        }

        private async Task<AssistantResponse> CreateAndRunAssistant(AssistantsClient client, AssistantRequest request)
        {
            OpenAIFile? uploadedAssistantFile = await HandleFileUpload(client, request.File);
            Assistant assistant = await CreateAssistant(client, request, uploadedAssistantFile);
            AssistantThread thread = (await client.CreateThreadAsync()).Value;
            ThreadMessage messageResponse = (await client.CreateMessageAsync(thread.Id, MessageRole.User, request.UserCommand)).Value;

            ThreadRun run = await CreateRun(client, thread, assistant, request.AdditionalCommand);
            await WaitForRunToComplete(client, thread, run);

            var messages = await FetchMessages(client, thread);
            var response = new AssistantResponse();
            await ProcessMessageContent(client, messages.First(), response);

            // cleanup
            await client.DeleteAssistantAsync(assistant.Id);
            await client.DeleteThreadAsync(thread.Id);
            if (uploadedAssistantFile != null)
            {
                await client.DeleteFileAsync(uploadedAssistantFile.Id);
            }

            return (response);
        }

        private async Task<OpenAIFile?> HandleFileUpload(AssistantsClient client, IFormFile? file)
        {
            if (file == null || file.Length <= 0) return null;

            var uploadResponse = await client.UploadFileAsync(file.OpenReadStream(), OpenAIFilePurpose.Assistants);
            return uploadResponse.Value;
        }

        private async Task<Assistant> CreateAssistant(AssistantsClient client, AssistantRequest request, OpenAIFile? uploadedAssistantFile)
        {
            var creationOptions = new AssistantCreationOptions("gpt4turbo")
            {
                Name = "GPTDeploymentName",
                Instructions = request.SystemCommand,
                Tools = { new CodeInterpreterToolDefinition() }
            };
            if (uploadedAssistantFile != null)
            {
                creationOptions.FileIds.Add(uploadedAssistantFile.Id);
            }

            var assistantResponse = await client.CreateAssistantAsync(creationOptions);
            return assistantResponse.Value;
        }

        private async Task<ThreadMessage> CreateMessage(AssistantsClient client, AssistantThread thread, string userCommand)
        {
            var messageResponse = await client.CreateMessageAsync(thread.Id, MessageRole.User, userCommand);
            return messageResponse.Value;
        }

        private async Task<ThreadRun> CreateRun(AssistantsClient client, AssistantThread thread, Assistant assistant, string? additionalCommand)
        {
            var options = new CreateRunOptions(assistant.Id) { AdditionalInstructions = additionalCommand };
            var runResponse = await client.CreateRunAsync(thread.Id, options);
            return runResponse.Value;
        }

        private async Task WaitForRunToComplete(AssistantsClient client, AssistantThread thread, ThreadRun run)
        {
            while (run.Status == RunStatus.Queued || run.Status == RunStatus.InProgress)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500));
                run = (await client.GetRunAsync(thread.Id, run.Id)).Value;
            }
        }

        private async Task<IReadOnlyList<ThreadMessage>> FetchMessages(AssistantsClient client, AssistantThread thread)
        {
            var afterRunMessagesResponse = await client.GetMessagesAsync(thread.Id);
            return afterRunMessagesResponse.Value.Data;
        }

        private async Task ProcessMessageContent(AssistantsClient client, ThreadMessage message, AssistantResponse response)
        {
            foreach (MessageContent contentItem in message.ContentItems)
            {
                if (contentItem is MessageTextContent textItem)
                {
                    response.Text = textItem.Text;
                }
                else if (contentItem is MessageImageFileContent imageFileItem)
                {
                    response.ImageUrl = await GetBlobImageUrl(await client.GetFileContentAsync(imageFileItem.FileId));
                    await client.DeleteFileAsync(imageFileItem.FileId);
                }
            }
        }

        private async Task<string> GetBlobImageUrl(BinaryData binaryData)
        {
            using var stream = binaryData.ToStream();

            BlobContainerClient blobContainerClient = new BlobContainerClient(_blobKey, "output-images"); // di
            string newName = Guid.NewGuid().ToString() + "-gpt.png";
            BlobClient blobClient = blobContainerClient.GetBlobClient(newName);

            if (!await blobClient.ExistsAsync())
            {
                await blobClient.UploadAsync(stream, new BlobHttpHeaders { ContentType = "image/png" });
            }

            return blobClient.Uri.AbsoluteUri;
        }
    }
}