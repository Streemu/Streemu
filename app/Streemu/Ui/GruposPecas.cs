using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// Painel que posiciona os filhos à mão, pela largura que recebe, e devolve a altura que precisou. Existe porque o FlowLayoutPanel cortava e empilhava os
/// botões do cartão do grupo (teste de primeiro uso, 2026-11-19): aqui a regra é explícita (cabe ao lado ou desce de linha) e tudo é medido em dp.
/// Vai dentro de um <see cref="CartaoDeAjuste"/>, que mede o filho por <see cref="GetPreferredSize"/>.
/// </summary>
abstract class PainelManual : Panel
{
    /// <summary>Altura mínima: dois cartões lado a lado ficam da mesma altura mesmo com textos de tamanhos diferentes.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int AlturaMinima { get => _alturaMinima; set { if (_alturaMinima != value) { _alturaMinima = value; PerformLayout(); } } }
    int _alturaMinima;

    protected int Dp(int valor) => UiScale.Dp(this, valor);

    /// <summary>O Windows põe um recuo antes da letra de um rótulo (2 dp, o mesmo de <see cref="AjusteBlocos"/>): rótulos começam esse tanto antes para a letra alinhar com a borda de campos e botões.</summary>
    protected int Recuo => Dp(2);

    /// <summary>Posiciona os filhos para <paramref name="largura"/> (só mede se <paramref name="aplicar"/> for falso) e devolve a altura.</summary>
    protected abstract int Posicionar(int largura, bool aplicar);

    public override Size GetPreferredSize(Size proposedSize)
    {
        int largura = proposedSize.Width > 0 ? proposedSize.Width : Width;
        return new Size(largura, Math.Max(AlturaMinima, Posicionar(largura, false)));
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (Width <= 0) return;
        int altura = Math.Max(AlturaMinima, Posicionar(Width, true));
        if (Height != altura) Height = altura; // o cartão acompanha pela medida; aqui a altura real, já que o encaixe em cima não a calcula
    }

    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); PerformLayout(); }

    /// <summary>Altura de uma linha de texto de <paramref name="rotulo"/> (a fonte dele, mais uma folga contra corte de letra com cauda).</summary>
    protected int LinhaDe(Label rotulo) => rotulo.Font.Height + Dp(2);
}

/// <summary>Um cartão de "Criar um grupo" / "Entrar com um convite": título, uma frase de apoio opcional e o campo com o botão (lado a lado se couber, senão um embaixo do outro).</summary>
sealed class ConteudoAcao : PainelManual
{
    readonly Label _titulo, _nota;
    readonly CampoArredondado _campo;
    readonly Button _botao;
    bool _comNota = true;

    /// <summary>Altura que a frase de apoio ocupa no mínimo: com dois cartões lado a lado, o campo e o botão dos dois ficam na mesma linha mesmo que uma frase quebre e a outra não.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int AlturaNotaMinima { get => _notaMinima; set { if (_notaMinima != value) { _notaMinima = value; PerformLayout(); } } }
    int _notaMinima;

    /// <summary>Altura que a frase de apoio pede numa largura de conteúdo (para o par de cartões combinar a maior).</summary>
    public int AlturaDaNota(int largura) => _comNota ? _nota.GetPreferredSize(new Size(largura, 0)).Height : 0;

    public ConteudoAcao(string titulo, string nota, CampoArredondado campo, Button botao)
    {
        _titulo = AjusteBlocos.Legenda(titulo);
        _nota = new Label { Text = nota, AutoSize = true, ForeColor = SystemColors.GrayText };
        _campo = campo; _botao = botao;
        Controls.AddRange([_titulo, _nota, _campo, _botao]);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Titulo { get => _titulo.Text; set { if (_titulo.Text != value) { _titulo.Text = value; PerformLayout(); } } }

    /// <summary>A frase de apoio só aparece quando a página tem espaço de sobra (sem grupo nenhum); com grupos o cartão fica compacto.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ComNota { get => _comNota; set { if (_comNota != value) { _comNota = value; _nota.Visible = value; PerformLayout(); } } }

    protected override int Posicionar(int largura, bool aplicar)
    {
        int gap = Dp(10), y = 0;
        var tam = new Size(largura, 0);
        int alturaTitulo = _titulo.GetPreferredSize(tam).Height;
        if (aplicar) { _titulo.MaximumSize = tam; _titulo.Location = new Point(-Recuo, y); }
        y += alturaTitulo + Dp(4);
        if (_comNota)
        {
            int alturaNota = Math.Max(AlturaNotaMinima, _nota.GetPreferredSize(tam).Height);
            if (aplicar) { _nota.MaximumSize = tam; _nota.Location = new Point(-Recuo, y); }
            y += alturaNota + gap;
        }
        int altura = Math.Max(_campo.Height, Dp(34)), larguraBotao = _botao.GetPreferredSize(Size.Empty).Width;
        if (largura >= Dp(200) + gap + larguraBotao)
        {
            if (aplicar) { _campo.SetBounds(0, y, largura - larguraBotao - gap, altura); _botao.SetBounds(largura - larguraBotao, y, larguraBotao, altura); }
            y += altura;
        }
        else
        {
            if (aplicar) _campo.SetBounds(0, y, largura, altura);
            y += altura + gap;
            if (aplicar) _botao.SetBounds(0, y, larguraBotao, altura);
            y += altura;
        }
        return y;
    }
}

/// <summary>
/// O miolo do cartão de um grupo: nome (com reticências se não couber) e "N pessoas · você é ..." à esquerda, os botões à direita; se a largura não der para
/// os dois, os botões descem para baixo do texto. Para o dono, embaixo, as pessoas do grupo com "Remover".
/// </summary>
sealed class ConteudoGrupo : PainelManual
{
    readonly Label _nome, _detalhe;
    readonly Button _principal, _secundario;
    readonly Label? _pessoas;
    readonly Panel? _linha;
    readonly List<(Label Nome, Button Remover)> _membros;

    public ConteudoGrupo(Label nome, Label detalhe, Button principal, Button secundario, IEnumerable<(Label, Button)> membros)
    {
        _nome = nome; _detalhe = detalhe; _principal = principal; _secundario = secundario;
        _membros = membros.ToList();
        Controls.AddRange([_nome, _detalhe, _principal, _secundario]);
        if (_membros.Count > 0)
        {
            _pessoas = AjusteBlocos.Subtitulo("Pessoas do grupo");
            _linha = new Panel { AccessibleRole = AccessibleRole.Separator };
            Controls.AddRange([_linha, _pessoas]);
            foreach (var (n, r) in _membros) Controls.AddRange([n, r]);
        }
        _nome.FontChanged += (_, _) => PerformLayout();
        _detalhe.FontChanged += (_, _) => PerformLayout();
    }

    /// <summary>
    /// "Sair" (membro) é mais estreito que "Mais ▾" (dono): sem igualar, o "Copiar convite" mudava de lugar de um cartão para outro (achado de 2026-11-24).
    /// Os dois ficam com a largura do mais largo, medida na fonte do botão. Não parte do tamanho preferido do botão: com AutoSize desligado ele devolve o
    /// tamanho atual, e somar a ele a cada arrumação fazia o "Sair" crescer até ocupar o cartão (visto na tela, 2026-11-24).
    /// </summary>
    int LarguraDoSegundo()
    {
        var fonte = _secundario.Font;
        int largo = Math.Max(TextRenderer.MeasureText("Mais ▾", fonte).Width, TextRenderer.MeasureText("Sair", fonte).Width);
        return largo + Dp(36);
    }

    protected override int Posicionar(int largura, bool aplicar)
    {
        int gap = Dp(8), margem = Dp(16);
        var p = _principal.GetPreferredSize(Size.Empty); var s = _secundario.GetPreferredSize(Size.Empty);
        s.Width = LarguraDoSegundo();
        int alturaBotao = Math.Max(Dp(34), Math.Max(p.Height, s.Height));
        int botoes = p.Width + gap + s.Width;
        int alturaTexto = LinhaDe(_nome) + Dp(2) + LinhaDe(_detalhe);
        int y;
        if (largura - botoes - margem >= Dp(200))
        {
            int linha = Math.Max(alturaTexto, alturaBotao), larguraTexto = largura - botoes - margem;
            if (aplicar)
            {
                int ty = (linha - alturaTexto) / 2, by = (linha - alturaBotao) / 2;
                _nome.SetBounds(-Recuo, ty, larguraTexto + Recuo, LinhaDe(_nome));
                _detalhe.SetBounds(-Recuo, ty + LinhaDe(_nome) + Dp(2), larguraTexto + Recuo, LinhaDe(_detalhe));
                _principal.SetBounds(largura - botoes, by, p.Width, alturaBotao);
                _secundario.SetBounds(largura - s.Width, by, s.Width, alturaBotao);
            }
            y = linha;
        }
        else
        {
            if (aplicar)
            {
                _nome.SetBounds(-Recuo, 0, largura + Recuo, LinhaDe(_nome));
                _detalhe.SetBounds(-Recuo, LinhaDe(_nome) + Dp(2), largura + Recuo, LinhaDe(_detalhe));
            }
            y = alturaTexto + Dp(10);
            if (aplicar) _principal.SetBounds(0, y, Math.Min(p.Width, largura), alturaBotao);
            // os dois botões não cabem na mesma linha (página estreita com o Ctrl +): o segundo desce, em vez de sair cortado ("Mais ▾" com 50 px, --grupos-check 2026-11-20)
            bool juntos = p.Width + gap + s.Width <= largura;
            if (!juntos) y += alturaBotao + gap;
            if (aplicar) _secundario.SetBounds(juntos ? p.Width + gap : 0, y, Math.Min(s.Width, largura), alturaBotao);
            y += alturaBotao;
        }
        if (_membros.Count == 0 || _linha == null || _pessoas == null) return y;
        y += Dp(14);
        if (aplicar) _linha.SetBounds(0, y, largura, Math.Max(1, Dp(1)));
        y += Math.Max(1, Dp(1)) + Dp(4);
        var capt = _pessoas.GetPreferredSize(Size.Empty);
        if (aplicar) _pessoas.Location = new Point(-Recuo, y);
        y += capt.Height;
        foreach (var (nome, remover) in _membros)
        {
            var r = remover.GetPreferredSize(Size.Empty);
            int alturaLinha = Math.Max(Dp(30), r.Height);
            if (aplicar)
            {
                remover.SetBounds(largura - r.Width, y, r.Width, alturaLinha);
                nome.SetBounds(-Recuo, y + (alturaLinha - LinhaDe(nome)) / 2, Math.Max(1, largura - r.Width - gap + Recuo), LinhaDe(nome));
            }
            y += alturaLinha + Dp(4);
        }
        return y - Dp(4);
    }
}
