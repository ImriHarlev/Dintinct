using Shared.Contracts.Enums;
using Shared.Contracts.Interfaces;

namespace NetworkB.Activities.Reporting.Services;

public interface IAnswerDispatcherFactory
{
    IAnswerDispatcher GetDispatcher(AnswerType answerType);
}

public class AnswerDispatcherFactory : IAnswerDispatcherFactory
{
    private readonly IServiceProvider _serviceProvider;

    public AnswerDispatcherFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public IAnswerDispatcher GetDispatcher(AnswerType answerType) => answerType switch
    {
        AnswerType.RabbitMQ => _serviceProvider.GetRequiredKeyedService<IAnswerDispatcher>("RabbitMQ"),
        AnswerType.FileSystem => _serviceProvider.GetRequiredKeyedService<IAnswerDispatcher>("FileSystem"),
        _ => throw new ArgumentOutOfRangeException(nameof(answerType), answerType, "Unknown answer type")
    };
}
