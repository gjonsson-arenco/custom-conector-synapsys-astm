using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Synapsys.Connector.Astm;
using Synapsys.Connector.Configuration;

namespace Synapsys.Connector.Tests;

/// <summary>
/// Para bajar una peticion hay que saber si el equipo esta hablando sin robarle bytes. Estos tests
/// fijan que esperar el silencio no consume nada y que un timeout no pierde el byte que llega despues.
/// </summary>
public sealed class IdleAwareConnectionTests
{
    [Fact]
    public async Task Esperar_con_la_linea_en_silencio_no_pierde_el_byte_que_llega_despues()
    {
        var socket = new FakeSocket();
        var line = new IdleAwareConnection(socket, CancellationToken.None);

        (await line.WaitForDataAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None)).ShouldBeFalse();
        line.HasData.ShouldBeFalse();

        socket.Push(ControlChars.ENQ);

        (await line.WaitForDataAsync(TimeSpan.FromSeconds(5), CancellationToken.None)).ShouldBeTrue();
        line.HasData.ShouldBeTrue();
        (await line.ReadByteAsync(CancellationToken.None)).ShouldBe(ControlChars.ENQ);
        line.HasData.ShouldBeFalse();
    }

    [Fact]
    public async Task Un_timeout_de_lectura_no_pierde_el_byte_siguiente()
    {
        var socket = new FakeSocket();
        var line = new IdleAwareConnection(socket, CancellationToken.None);

        using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            await Should.ThrowAsync<OperationCanceledException>(async () => await line.ReadByteAsync(timeout.Token));
        }

        socket.Push(ControlChars.ACK);

        (await line.ReadByteAsync(CancellationToken.None)).ShouldBe(ControlChars.ACK);
    }

    [Fact]
    public async Task Si_el_equipo_pide_la_linea_al_mismo_tiempo_se_le_contesta_ACK_y_se_recibe_lo_suyo()
    {
        var socket = new FakeSocket();
        var line = new IdleAwareConnection(socket, CancellationToken.None);
        var channel = new LowLevelChannel(line, new AstmOptions(), AstmSeparators.Default, NullLogger.Instance);

        // El equipo contesta nuestro ENQ con el suyo: tiene prioridad.
        socket.Push(ControlChars.ENQ);

        var outcome = await channel.SendAsync([AstmRecord.Create('H', AstmSeparators.Default)], CancellationToken.None);

        outcome.ShouldBe(SendOutcome.Contention);
        socket.Written.ShouldBe([ControlChars.ENQ]);

        // Sin esperar otro ENQ: ACK en el acto y se recibe la transmision del equipo.
        var receiving = channel.ReceiveAsync(CancellationToken.None);
        socket.Push(new Frame(1, "Q|1|^B100\r").ToBytes(useChecksum: true));
        socket.Push(ControlChars.EOT);

        var received = (await receiving).ShouldNotBeNull();
        received.ShouldHaveSingleItem().Type.ShouldBe('Q');
        socket.Written.ShouldBe([ControlChars.ENQ, ControlChars.ACK, ControlChars.ACK]);
    }

    [Fact]
    public async Task El_envio_low_level_termina_en_Sent_cuando_el_equipo_acepta_todo()
    {
        var socket = new FakeSocket();
        var line = new IdleAwareConnection(socket, CancellationToken.None);
        var channel = new LowLevelChannel(line, new AstmOptions(), AstmSeparators.Default, NullLogger.Instance);

        // ACK al ENQ y a los dos frames.
        socket.Push(ControlChars.ACK, ControlChars.ACK, ControlChars.ACK);

        var outcome = await channel.SendAsync(
            [AstmRecord.Create('H', AstmSeparators.Default), AstmRecord.Create('L', AstmSeparators.Default)],
            CancellationToken.None);

        outcome.ShouldBe(SendOutcome.Sent);
        socket.Written.First().ShouldBe(ControlChars.ENQ);
        socket.Written.Last().ShouldBe(ControlChars.EOT);
    }

    /// <summary>Socket de mentira: lo que se empuja se lee de a un byte; lo escrito queda anotado.</summary>
    private sealed class FakeSocket : IAstmConnection
    {
        private readonly Channel<int> _incoming = Channel.CreateUnbounded<int>();

        public List<byte> Written { get; } = [];

        public string Remote => "fake";

        public void Push(params byte[] bytes)
        {
            foreach (var b in bytes)
            {
                _incoming.Writer.TryWrite(b);
            }
        }

        public async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken) =>
            await _incoming.Reader.ReadAsync(cancellationToken);

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            Written.AddRange(data.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
