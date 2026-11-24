using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// A imagem da denúncia (spec 029, 6.1): só a janela do player daquela live, em JPEG de no máximo 1280 px de largura e 300 KB. Fica na memória (nunca vai ao disco) e
/// <b>nada aqui escreve em log</b> (spec 029, 15.16). Só é tirada DEPOIS do Enviar e nunca em abuso infantil (quem chama confere <see cref="Net.Denuncia.PodeAnexarImagem"/>
/// antes: com essa categoria nem se chega aqui).
/// </summary>
public static class DenunciaCaptura
{
    public const int LarguraMax = 1280, BytesMax = 300 * 1024;
    /// <summary>O compositor leva um quadro para tirar as janelas de cima (a barra, o cabeçalho e a janela da denúncia) antes de a tela ser lida.</summary>
    const int EsperaRedesenhoMs = 80;
    const uint PwClientOnly = 1, PwRenderFullContent = 2, GaRoot = 2;
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    /// <summary>
    /// Tira a imagem da janela do player. Chamar na thread da tela, SÓ depois do Enviar. Começa por <paramref name="esconder"/> (tira a barra de controles e o cabeçalho de
    /// cima; a janela da denúncia já está fechada) e espera o redesenho. Depois tenta o <c>PrintWindow</c> da área do vídeo; se vier toda preta (o ffplay em Vulkan ou D3D costuma
    /// devolver preto), copia da tela os limites do vídeo, mas só se os pontos do vídeo mostram mesmo o player (<see cref="VideoLivre"/>): outra janela na frente vira "sem imagem".
    /// Preto nos dois caminhos = sem imagem (nulo).
    /// </summary>
    public static byte[]? Capturar(IntPtr janela, Rectangle video, Action esconder)
    {
        try
        {
            esconder();
            Thread.Sleep(EsperaRedesenhoMs);
            using (var impressa = Imprimir(janela))
                if (impressa != null && !Escura(impressa)) return ParaJpeg(impressa);
            if (video.Width < 16 || video.Height < 16) return null;
            if (!VideoLivre(janela, video, p => WindowFromPoint(p), h => GetAncestor(h, GaRoot))) return null;
            using var tela = DaTela(video);
            return tela != null && !Escura(tela) ? ParaJpeg(tela) : null;
        }
        catch { return null; } // sem imagem a denúncia segue; o motivo não vai a lugar nenhum
    }

    /// <summary>
    /// O <c>CopyFromScreen</c> lê o que está na tela, não a janela do player: se outra janela cobre o vídeo, a imagem sairia dela (e poderia ser de outro assunto). Confere o centro e
    /// quatro pontos internos (a um quarto das bordas): em todos, a janela visível tem que ser o player, uma filha dele ou ter a mesma raiz (a janela de streams que o hospeda).
    /// <paramref name="noPonto"/> e <paramref name="raiz"/> são o <c>WindowFromPoint</c> e o <c>GetAncestor(GA_ROOT)</c> (trocados por falsos nos testes).
    /// </summary>
    public static bool VideoLivre(IntPtr janela, Rectangle video, Func<Point, IntPtr> noPonto, Func<IntPtr, IntPtr> raiz)
    {
        if (janela == IntPtr.Zero || video.Width < 16 || video.Height < 16) return false;
        var raizDoPlayer = raiz(janela);
        if (raizDoPlayer == IntPtr.Zero) raizDoPlayer = janela;
        var pontos = new[]
        {
            new Point(video.X + video.Width / 2, video.Y + video.Height / 2),
            new Point(video.X + video.Width / 4, video.Y + video.Height / 4),
            new Point(video.X + video.Width * 3 / 4, video.Y + video.Height / 4),
            new Point(video.X + video.Width / 4, video.Y + video.Height * 3 / 4),
            new Point(video.X + video.Width * 3 / 4, video.Y + video.Height * 3 / 4),
        };
        foreach (var ponto in pontos)
        {
            var visivel = noPonto(ponto);
            if (visivel == IntPtr.Zero) return false;
            if (visivel == janela || raiz(visivel) == raizDoPlayer) continue;
            return false;
        }
        return true;
    }

    /// <summary>A área do vídeo da janela pelo <c>PrintWindow</c> (só o cliente, sem moldura; com o conteúdo de janelas DirectX quando dá).</summary>
    public static Bitmap? Imprimir(IntPtr janela)
    {
        if (janela == IntPtr.Zero || Native.IsIconic(janela) || !Native.GetClientRect(janela, out var r) || r.Width < 16 || r.Height < 16) return null;
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
        try
        {
            bool ok;
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try { ok = PrintWindow(janela, hdc, PwClientOnly | PwRenderFullContent); }
                finally { g.ReleaseHdc(hdc); } // o Graphics fica preso ao HDC até aqui, com ou sem erro
            }
            if (!ok) { bmp.Dispose(); return null; }
            return bmp;
        }
        catch { bmp.Dispose(); throw; } // quem chama trata a falha; o Bitmap não pode ficar para o coletor
    }

    static Bitmap? DaTela(Rectangle area)
    {
        var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
        try
        {
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(area.Location, Point.Empty, area.Size);
            return bmp;
        }
        catch { bmp.Dispose(); throw; }
    }

    /// <summary>Quase tudo preto? Olha uma grade de 12 x 8 pontos: menos de 3 claros é "preto" (como a miniatura do Transmitir).</summary>
    public static bool Escura(Bitmap b)
    {
        int claros = 0;
        for (int i = 1; i <= 12; i++)
            for (int j = 1; j <= 8; j++)
            {
                var c = b.GetPixel(b.Width * i / 13, b.Height * j / 9);
                if (c.R + c.G + c.B > 30) claros++;
            }
        return claros < 3;
    }

    /// <summary>
    /// JPEG de no máximo <paramref name="larguraMax"/> px de largura e <paramref name="bytesMax"/> bytes. Reduz a largura ao teto e, se passar do peso, baixa a qualidade
    /// (85 até 40); ainda pesado, encolhe a imagem em 20% e tenta as qualidades de novo. Nulo se nem a imagem mínima (160 px) cabe.
    /// </summary>
    public static byte[]? ParaJpeg(Image origem, int larguraMax = LarguraMax, int bytesMax = BytesMax)
    {
        var codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
        if (codec == null || origem.Width <= 0 || origem.Height <= 0) return null;
        double razao = (double)origem.Height / origem.Width;
        for (int largura = Math.Min(origem.Width, larguraMax); largura >= 160; largura = (int)(largura * 0.8))
        {
            int altura = Math.Max(1, (int)Math.Round(largura * razao));
            using var reduzida = new Bitmap(largura, altura, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(reduzida))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(origem, new Rectangle(0, 0, largura, altura));
            }
            foreach (long qualidade in new long[] { 85, 75, 65, 55, 45, 40 })
            {
                using var ms = new MemoryStream();
                using var parametros = new EncoderParameters(1);
                parametros.Param[0] = new EncoderParameter(Encoder.Quality, qualidade);
                reduzida.Save(ms, codec, parametros);
                if (ms.Length <= bytesMax) return ms.ToArray();
            }
        }
        return null;
    }
}
