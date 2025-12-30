public class ModelRouter;
{
    public string RouteToBestModel(string userCommand)
    {
        var commandType = AnalyzeCommandType(userCommand);
        
        return commandType switch;
        {
            CommandType.GameDevelopment => "models/codellama-13b-instruct.Q4_K_M.gguf",
            CommandType.PythonScripting => "models/wizardcoder-python-34b.q4_k_m.gguf", 
            CommandType.QuickResponse // Dependency Injection ile kullanım;
services.AddSingleton<IModelRouter, ModelRouter>();
services.AddSingleton<IModelRegistry, ModelRegistry>();

// Model endpoint kaydı;
var registration = new ModelRegistration;
{
    ModelId = "bert-classification-001",
    ModelName = "BERT-Classification",
    ModelType = "NLP",
    Version = "1.2.0",
    EndpointType = ModelRouter.EndpointType.CloudService,
    EndpointUrl = "https://api.models.example.com/bert/v1",
    Metadata = new Dictionary<string, object>
    {
        { "framework", "TensorFlow" },
        { "precision", "float16" }
    },
    SupportedTasks = new List<string> { "text_classification", "sentiment_analysis" }
};

var result = await modelRouter.RegisterModelEndpointAsync(registration);

// Model yönlendirme;
var request = new ModelRoutingRequest;
{
    ModelName = "BERT-Classification",
    TaskType = "text_classification",
    Parameters = new Dictionary<string, object>
    {
        { "text", "This product is amazing!" }
    },
    PreferredStrategy = ModelRouter.LoadBalancingStrategy.PerformanceBased;
};

var routingResult = await modelRouter.RouteRequestAsync(request);
if (routingResult.IsSuccess)
        {
            var endpoint = routingResult.Endpoint;
            // Model çağrısı yap;
        }

        // İstatistikler;
        var stats = modelRouter.GetStatistics();=> "models/mistral-7;
