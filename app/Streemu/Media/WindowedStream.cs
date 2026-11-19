namespace Streemu.Media;

/// <summary>
/// Passa os bytes adiante só durante uma janela curta a cada intervalo (e descarta o resto). A miniatura precisa de UM quadro-chave a cada 8 s,
/// mas o encoder solta um a cada 2 s; sem isto o ffmpeg da miniatura decodificava quatro vezes mais do que usa (medido em 2026-11-08, Bloodborne
/// 1080p60: ~2,4% de um núcleo contra a meta de 1%). Os pacotes TS chegam inteiros; se a janela abre no meio de um quadro, o pedaço é descartado
/// pelo demuxer e vale o quadro-chave seguinte (a janela é maior que o intervalo entre quadros-chave de 2 s).
/// Uso: <c>new WindowedStream(stdin, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(2.4))</c>
/// </summary>
sealed class WindowedStream : Stream
{
    readonly Stream _inner;
    readonly long _periodTicks, _openTicks;
    readonly long _origin = System.Diagnostics.Stopwatch.GetTimestamp();

    public WindowedStream(Stream inner, TimeSpan period, TimeSpan open)
    {
        _inner = inner;
        _periodTicks = (long)(period.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
        _openTicks = (long)(open.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
    }

    bool IsOpen => (System.Diagnostics.Stopwatch.GetTimestamp() - _origin) % _periodTicks < _openTicks;

    public override void Write(byte[] buffer, int offset, int count) { if (IsOpen) _inner.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { if (IsOpen) _inner.Write(buffer); }
    public override void Flush() => _inner.Flush();
    protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
