using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synapsys.Connector.Configuration;

namespace Synapsys.Connector.Astm;

/// <summary>Arma el canal ASTM (low o high level) que corresponde a la configuracion.</summary>
public sealed class AstmChannelFactory
{
    private readonly AstmOptions _options;
    private readonly AstmSeparators _separators;
    private readonly ILoggerFactory _loggerFactory;

    public AstmChannelFactory(IOptions<AstmOptions> options, ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _separators = new AstmSeparators(
            First(_options.FieldSeparator, '|'),
            First(_options.ComponentSeparator, '^'),
            First(_options.RepeatSeparator, '\\'));
    }

    public AstmSeparators Separators => _separators;

    public IAstmChannel Create(IAstmConnection connection) => _options.IsHighLevel
        ? new HighLevelChannel(connection, _options, _separators, _loggerFactory.CreateLogger<HighLevelChannel>())
        : new LowLevelChannel(connection, _options, _separators, _loggerFactory.CreateLogger<LowLevelChannel>());

    private static char First(string value, char fallback) =>
        string.IsNullOrEmpty(value) ? fallback : value[0];
}
