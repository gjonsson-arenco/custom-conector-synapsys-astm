using Microsoft.Extensions.Logging;
using Synapsys.Connector.Configuration;

namespace Synapsys.Connector.Astm;

/// <summary>Arma el canal ASTM (low o high level) que corresponde a la configuracion.</summary>
public sealed class AstmChannelFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public AstmChannelFactory(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

    public IAstmChannel Create(IAstmConnection connection, AstmOptions options, AstmSeparators separators) => options.IsHighLevel
        ? new HighLevelChannel(connection, options, separators, _loggerFactory.CreateLogger<HighLevelChannel>())
        : new LowLevelChannel(connection, options, separators, _loggerFactory.CreateLogger<LowLevelChannel>());

    public static AstmSeparators SeparatorsFor(AstmOptions options) => new(
        First(options.FieldSeparator, '|'),
        First(options.ComponentSeparator, '^'),
        First(options.RepeatSeparator, '\\'));

    private static char First(string value, char fallback) =>
        string.IsNullOrEmpty(value) ? fallback : value[0];
}
