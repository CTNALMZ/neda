// NEDA.EngineIntegration/Unreal/CodellamaIntegration.cs;
public class UnrealCodeGenerator;
{
    public async Task<string> GenerateBlueprint(string description)
    {
        var prompt = $"""
        Create Unreal Engine Blueprint script for: {description}
        Requirements:
        - Use event-driven structure;
        - Include proper variable types;
        - Add comments for clarity;
        - Follow Unreal best practices;
        
        Blueprint:
        """;
        
        return await _codellama.GenerateCode(prompt);
    }
    
    public async Task<string> GenerateCppClass(string className, string functionality)
    {
        var prompt = $"""
        Create Unreal Engine C++ class for: {functionality}
        Class name: {className}
        Requirements:
        - Use UCLASS() macros;
        - Include proper header includes;
        - Follow Unreal coding standards;
        - Add UFUNCTION() where needed;
        
        Code:
        """;
        
        return await _codellama.GenerateCode(prompt);
    }
}
