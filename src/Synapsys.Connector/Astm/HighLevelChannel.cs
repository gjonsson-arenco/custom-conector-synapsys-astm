using System.Text;
using Microsoft.Extensions.Logging;
using Synapsys.Connector.Configuration;

namespace Synapsys.Connector.Astm;

/// <summary>
/// ASTM high-level: la transmision completa viaja envuelta en VT ... FS CR, sin ENQ/ACK ni
/// checksum. Los registros van separados por CR dentro del envoltorio.
/// </summary>
public sealed class HighLevelChannel : IAstmChannel
{
    private readonly IAstmConnection _connection;
    private readonly AstmOptions _options;
    private readonly AstmSeparators _separators;
    private readonly ILogger _logger;
    private readonly TimeSpan _timeout;

    public HighLevelChannel(IAstmConnection connection, AstmOptions options, AstmSeparators separators, ILogger logger)
    {
        _connection = connection;
        _options = options;
        _separators = separators;
        _logger = logger;
        _timeout = TimeSpan.FromSeconds(Math.Max(1, options.ReceiveTimeoutSeconds));
    }

    public async Task<IReadOnlyList<AstmRecord>?> ReceiveAsync(CancellationToken cancellationToken)
    {
        // Espera indefinida al inicio del mensaje (VT).
        while (true)
        {
            var first = await _connection.ReadByteAsync(cancellationToken);
            if (first < 0)
            {
                return null;
            }

            if (first == ControlChars.VT)
            {
                break;
            }
        }

        var buffer = new StringBuilder();

        try
        {
            while (true)
            {
                var b = await ReadWithTimeoutAsync(cancellationToken);
                if (b < 0)
                {
                    return null;
                }

                if (b == ControlChars.FS)
                {
                    // Consumir el CR final del envoltorio, si viene.
                    await ReadWithTimeoutAsync(cancellationToken);
                    break;
                }

                buffer.Append((char)b);
            }
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Timeout recibiendo la transmision high-level. Se descarta.");
            return null;
        }

        return SplitRecords(buffer.ToString());
    }

    public async Task<bool> SendAsync(IReadOnlyList<AstmRecord> records, CancellationToken cancellationToken)
    {
        var message = new StringBuilder();
        message.Append((char)ControlChars.VT);

        foreach (var record in records)
        {
            message.Append(record);
            message.Append((char)ControlChars.CR);
        }

        message.Append((char)ControlChars.FS);
        message.Append((char)ControlChars.CR);

        await _connection.WriteAsync(ControlChars.Encoding.GetBytes(message.ToString()), cancellationToken);
        return true;
    }

    private async ValueTask<int> ReadWithTimeoutAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        try
        {
            return await _connection.ReadByteAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    private IReadOnlyList<AstmRecord> SplitRecords(string text) =>
        text.Split((char)ControlChars.CR)
            .Select(line => line.Trim('\n'))
            .Where(line => line.Length > 0)
            .Select(line => AstmRecord.Parse(line, _separators))
            .ToList();
}
