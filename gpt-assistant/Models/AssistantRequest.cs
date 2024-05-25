namespace AzureOpenAIAssistant.Models
{
    public class AssistantRequest
    {
        public required string UserCommand { get; set; }
        public string? SystemCommand { get; set; }

        public string? AdditionalCommand { get; set;}
        public IFormFile? File { get; set; }
    }
}
