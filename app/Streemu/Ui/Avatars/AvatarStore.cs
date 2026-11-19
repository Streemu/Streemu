using System.Drawing.Imaging;
using Streemu.Util;

namespace Streemu.Ui.Avatars;

/// <summary>
/// O avatar que o próprio usuário escolheu no PC: a imagem para desenhar, o que vai para os amigos (<see cref="Payload"/>) e a cópia
/// guardada para a próxima abertura. O arquivo ORIGINAL nunca é guardado nem enviado: imagem parada vira uma miniatura PNG de
/// 64 px, e só o GIF animado pequeno (até 200 KB) segue como o próprio arquivo, porque o Windows não grava GIF animado de novo.
/// Sem pasta (testes automáticos) tudo fica só na memória.
/// Uso: <c>var r = store.Choose(path); if (r.Error != null) aviso(r.Error);</c>
/// </summary>
public sealed class AvatarStore
{
    public const int ThumbnailMaxBytes = 24 * 1024;
    // meu.gif só existe em quem escolheu um GIF pequeno nas primeiras horas dos avatares; ainda é lido, mas nunca mais gravado
    const string StillName = "meu.png", StripName = "meu.tira", LegacyGifName = "meu.gif";

    readonly string? _directory;

    /// <param name="directory">Onde guardar entre aberturas; nulo = só na memória.</param>
    public AvatarStore(string? directory) => _directory = directory;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Streemu", "avatar");

    public AvatarImage? Image { get; private set; }
    /// <summary>Os bytes que vão para os amigos (PNG de 64 px, ou o GIF pequeno); nulo se não há avatar.</summary>
    public byte[]? Payload { get; private set; }
    public AvatarFormat PayloadFormat { get; private set; }
    /// <summary>Identifica a imagem (16 hex do SHA-256 do <see cref="Payload"/>): quem já tem esse hash não precisa receber de novo.</summary>
    public string? Hash { get; private set; }
    public bool HasAvatar => Image != null;
    public event Action? Changed;

    /// <summary>Lê o arquivo, guarda a cópia e passa a usá-lo. Se falhar, o avatar anterior continua valendo.</summary>
    public AvatarLoad Choose(string path)
    {
        var load = AvatarDecoder.LoadFile(path);
        if (load.Image == null) return load;
        var (payload, format) = BuildPayload(load);
        if (payload.Length > ThumbnailMaxBytes && format == AvatarFormat.Png)
        {
            load.Image.Dispose();
            return AvatarLoad.Fail($"A miniatura ficou com {AvatarFormatDetector.FormatSize(payload.Length)}; o máximo é {AvatarFormatDetector.FormatSize(ThumbnailMaxBytes)}. Escolha outra imagem.");
        }
        Apply(load.Image, payload, format);
        string? saveProblem = Save(payload, format);
        return saveProblem == null ? load : load with { Notice = string.Join(" ", new[] { load.Notice, saveProblem }.Where(n => n != null)) };
    }

    /// <summary>Carrega o avatar guardado da última vez (se houver e ainda estiver inteiro).</summary>
    public void LoadSaved()
    {
        if (_directory == null) return;
        foreach (var (name, format) in new[] { (StripName, AvatarFormat.Strip), (LegacyGifName, AvatarFormat.Gif), (StillName, AvatarFormat.Png) })
        {
            string path = Path.Combine(_directory, name);
            if (!File.Exists(path)) continue;
            byte[] bytes = File.ReadAllBytes(path);
            var load = format == AvatarFormat.Strip ? AvatarFilmstrip.Decode(bytes) : AvatarDecoder.Load(bytes, AvatarOrigin.LocalFile);
            if (load.Image == null) { Log.Info($"O avatar guardado não abriu ({load.Error}); ignorado."); continue; }
            Apply(load.Image, bytes, format);
            return;
        }
    }

    public void Clear()
    {
        Apply(null, null, AvatarFormat.Unknown);
        if (_directory == null) return;
        foreach (var name in new[] { StillName, StripName, LegacyGifName }) TryDelete(Path.Combine(_directory, name));
    }

    /// <summary>Animado: a tira de quadros (qualquer GIF cabe). Parado: miniatura PNG. Descarta os quadrados usados para montar a tira.</summary>
    static (byte[] Payload, AvatarFormat Format) BuildPayload(AvatarLoad load)
    {
        if (load.Squares is { Length: > 1 } squares)
        {
            try { return (AvatarFilmstrip.Encode(squares, load.Image!.DelaysMs), AvatarFormat.Strip); }
            finally { foreach (var square in squares) square.Dispose(); }
        }
        using var ms = new MemoryStream();
        load.Image!.FrameAt(0).Save(ms, ImageFormat.Png);
        return (ms.ToArray(), AvatarFormat.Png);
    }

    void Apply(AvatarImage? image, byte[]? payload, AvatarFormat format)
    {
        var old = Image;
        Image = image;
        Payload = payload;
        PayloadFormat = format;
        Hash = payload == null ? null : AvatarMessage.HashOf(payload);
        Changed?.Invoke();
        old?.Dispose(); // depois do aviso: quem desenhava já trocou para a imagem nova
    }

    /// <summary>Guarda a cópia para a próxima abertura. Devolve o motivo se não conseguiu (o avatar continua valendo nesta sessão).</summary>
    string? Save(byte[] payload, AvatarFormat format)
    {
        if (_directory == null) return null;
        try
        {
            Directory.CreateDirectory(_directory);
            string name = format == AvatarFormat.Strip ? StripName : StillName;
            File.WriteAllBytes(Path.Combine(_directory, name), payload);
            foreach (var other in new[] { StillName, StripName, LegacyGifName }.Where(n => n != name)) TryDelete(Path.Combine(_directory, other));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Info("Não consegui guardar o avatar: " + e.Message);
            return $"Não consegui guardar o avatar para a próxima vez ({e.GetType().Name}); ele vale só até fechar o app.";
        }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Info("Não consegui apagar o avatar guardado: " + e.Message); }
    }
}
