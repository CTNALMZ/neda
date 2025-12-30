// NEDA.Brain/DecisionMaking/SolutionGenerator/Exceptions/OptionGenerationException.cs;

using System;

namespace NEDA.Brain.DecisionMaking.SolutionGenerator;
{
    public class OptionGenerationException : Exception
    {
        public string GenerationId { get; }
        public string ProblemId { get; }

        public OptionGenerationException(string message) : base(message) { }

        public OptionGenerationException(string message, Exception innerException)
            : base(message, innerException) { }

        public OptionGenerationException(string message, string generationId, string problemId)
            : base(message)
        {
            GenerationId = generationId;
            ProblemId = problemId;
        }
    }

    public class OptionCreatorInitializationException : OptionGenerationException;
    {
        public OptionCreatorInitializationException(string message) : base(message) { }
        public OptionCreatorInitializationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class OptionLearningException : OptionGenerationException;
    {
        public OptionLearningException(string message) : base(message) { }
        public OptionLearningException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
