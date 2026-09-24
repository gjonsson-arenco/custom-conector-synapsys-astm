using System.Text;
using Microsoft.Extensions.Logging;
using Synapsys.Connector.Configuration;

namespace Synapsys.Connector.Astm;

/// <summary>
/// ASTM low-level (E1381): establecimiento con ENQ/ACK, frames con checksum y cierre con EOT.
/// El otro extremo es mandante: ante una colision de ENQ cedemos y volvemos a recibir.
/// </summary>
/// <remarks>
/// En la colision el ENQ del otro extremo ya se leyo: el proximo <see cref="ReceiveAsync"/> lo
/// toma como recibido y contesta ACK en el acto, sin esperar otro.
/// </remarks>
public sealed class LowLevelChannel : IAstmChannel
{
    private readonly IAstmConnection _connection;
    private readonly AstmOptions _options;
    private readonly AstmSeparators _separators;
    private readonly ILogger _logger;
    private readonly TimeSpan _timeout;

    // El otro extremo pidio la linea mientras mandabamos nuestro ENQ.
    private bool _peerEnquired;

    public LowLevelChannel(IAstmConnection connection, AstmOptions options, AstmSeparators separators, ILogger logger)
    {
        _connection = connection;
        _options = options;
        _separators = separators;
        _logger = logger;
        _timeout = TimeSpan.FromSeconds(Math.Max(1, options.ReceiveTimeoutSeconds));
    }

    public async Task<IReadOnlyList<AstmRecord>?> ReceiveAsync(CancellationToken cancellationToken)
    {
        // Espera indefinida al ENQ del otro extremo: aca no metemos timeout.
        while (!_peerEnquired)
        {
            var first = await _connection.ReadByteAsync(cancellationToken);
            if (first < 0)
            {
                return null;
            }

            if (first == ControlChars.ENQ)
            {
                break;
            }

            if (first == ControlChars.EOT)
            {
                continue;
            }

            _logger.LogDebug("Descartado {Char} mientras esperaba <ENQ>.", ControlChars.Describe(first));
        }

        _peerEnquired = false;
        await WriteAsync(ControlChars.ACK, cancellationToken);

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

                if (b == ControlChars.EOT)
                {
                    break;
                }

                if (b == ControlChars.ENQ)
                {
                    await WriteAsync(ControlChars.ACK, cancellationToken);
                    continue;
                }

                if (b != ControlChars.STX)
                {
                    continue;
                }

                var raw = await ReadFrameAsync(cancellationToken);
                if (raw is null)
                {
                    return null;
                }

                var frame = Frame.Parse(raw, _options.UseChecksum);
                if (frame is null)
                {
                    _logger.LogWarning("Frame invalido o checksum incorrecto: se responde <NAK>.");
                    await WriteAsync(ControlChars.NAK, cancellationToken);
                    continue;
                }

                buffer.Append(frame.Data);
                await WriteAsync(ControlChars.ACK, cancellationToken);
            }
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Timeout recibiendo la transmision. Se descarta.");
            return null;
        }

        return SplitRecords(buffer.ToString());
    }

    public async Task<SendOutcome> SendAsync(IReadOnlyList<AstmRecord> records, CancellationToken cancellationToken)
    {
        try
        {
            await WriteAsync(ControlChars.ENQ, cancellationToken);
            var response = await ReadWithTimeoutAsync(cancellationToken);

            if (response == ControlChars.ENQ)
            {
                _logger.LogInformation("Colision de <ENQ>: cede el conector, el otro extremo es mandante.");
                _peerEnquired = true;
                return SendOutcome.Contention;
            }

            if (response != ControlChars.ACK)
            {
                _logger.LogWarning("El otro extremo no acepto el establecimiento: {Char}.", ControlChars.Describe(response));
                return SendOutcome.Failed;
            }

            var number = 1;

            foreach (var record in records)
            {
                var bytes = new Frame(number, record + "\r").ToBytes(_options.UseChecksum);
                var retries = 0;

                while (true)
                {
                    await _connection.WriteAsync(bytes, cancellationToken);
                    response = await ReadWithTimeoutAsync(cancellationToken);

                    if (response == ControlChars.ACK)
                    {
                        number = Frame.NextNumber(number);
                        break;
                    }

                    if (response == ControlChars.NAK && ++retries <= _options.MaxRetries)
                    {
                        continue;
                    }

                    _logger.LogWarning("Envio abortado tras {Char}.", ControlChars.Describe(response));
                    await WriteAsync(ControlChars.EOT, cancellationToken);
                    return SendOutcome.Failed;
                }
            }

            await WriteAsync(ControlChars.EOT, cancellationToken);
            return SendOutcome.Sent;
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Timeout enviando la transmision.");
            return SendOutcome.Failed;
        }
    }

    private async Task<byte[]?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var buffer = new List<byte> { ControlChars.STX };

        while (true)
        {
            var b = await ReadWithTimeoutAsync(cancellationToken);
            if (b < 0)
            {
                return null;
            }

            buffer.Add((byte)b);

            if (b == ControlChars.LF)
            {
                return [.. buffer];
            }

            if (buffer.Count > 8192)
            {
                _logger.LogWarning("Frame demasiado largo sin terminador: se descarta.");
                return null;
            }
        }
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

    private ValueTask WriteAsync(byte control, CancellationToken cancellationToken) =>
        _connection.WriteAsync(new[] { control }, cancellationToken);

    private IReadOnlyList<AstmRecord> SplitRecords(string text) =>
        text.Split((char)ControlChars.CR)
            .Select(line => line.Trim('\n'))
            .Where(line => line.Length > 0)
            .Select(line => AstmRecord.Parse(line, _separators))
            .ToList();
}
