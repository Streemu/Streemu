using System.Security.Cryptography;
using System.Text;

namespace Streemu.Telemetria;

/// <summary>
/// Cifra dos dados com gente dentro que ficam no PC (spec 018, D21, seção 7.1): campos de identidade do <c>estado.json</c>, histórico de arquivos, logs,
/// telemetria local, histórico de envios e gravador. A raiz é o DPAPI do Windows (<see cref="DataProtectionScope.CurrentUser"/>, o mesmo do
/// <see cref="Util.Cofre"/>): uma chave aleatória de 32 bytes fica em <c>chave-local.dat</c> protegida por ele, e cada linha é cifrada com AES-GCM
/// por essa chave. Cifrar a linha direto pelo DPAPI custaria ~150 bytes de cabeçalho por linha de log.
///
/// O que a cifra protege: outra conta do Windows, disco roubado, backup e sincronização com a nuvem (a chave só abre neste usuário e neste PC).
/// O que NÃO protege: um programa rodando como a própria pessoa (ele chama o mesmo DPAPI). A política diz isso.
///
/// Formato: uma linha de texto, <c>c1:</c> + base64(nonce 12 | tag 16 | cifrado). Sem quebra de linha dentro, então o arquivo continua sendo "uma
/// linha por registro" e o <see cref="Envio"/> segue lendo por posição (byte) como sempre; só decifra cada linha antes de mandar. Linha sem o prefixo é
/// texto antigo, em claro, e é lida como está (estado de antes da A8 continua abrindo e é regravado cifrado).
///
/// Chave que existe e não abre (perfil do Windows ainda carregando, arquivo de outro PC ou usuário, arquivo estragado): ela nunca é trocada na hora, porque
/// isso levaria a conta e os dados cifrados de antes. A abertura começa sem cifra e uma thread própria tenta de novo (3 vezes, ~10 s entre elas); se abrir, a
/// cifra passa a valer dali em diante. Se nenhuma abrir, a abertura é contada em <c>chave-local.falhas</c> (só um número); na 3ª seguida o arquivo vira
/// <c>chave-local.dat.antigo</c> e nasce uma chave nova, sem janela e sem pedir nada (um arquivo que nunca abre deixaria a cifra desligada para sempre).
/// Abrir zera a contagem. A identidade cifrada do estado antigo deixa de abrir e a pessoa entra como nova, como num PC novo.
/// Nunca lança e nunca escreve no <see cref="Util.Log"/> (o log usa este código); o motivo fica em <see cref="Falha"/>.
/// </summary>
public static class CofreLocal
{
    public const string Prefixo = "c1:";
    /// <summary>O que aparece no lugar de uma linha cifrada que este usuário não consegue abrir (outro usuário, outro PC, arquivo estragado).</summary>
    public const string LinhaIlegivel = "(linha cifrada que este PC não abre)";

    static readonly byte[] Entropia = Encoding.UTF8.GetBytes("Streemu.CofreLocal.v1");
    static readonly object _g = new();
    static byte[]? _chave;
    static bool _tentou;
    static bool? _forcado;
    static string? _pasta;

    /// <summary>Por que a chave não abriu nem foi criada (nulo = tudo certo). Sem chave os dados novos ficam em claro: perder o diagnóstico é pior do que o risco do DPAPI falhar.</summary>
    public static string? Falha { get { lock (_g) return _falha; } private set { lock (_g) _falha = value; } }
    static string? _falha;

    /// <summary>Verdadeiro se a chave está aberta agora (já tentou e tem a chave). Não dispara a abertura.</summary>
    public static bool ChaveAberta { get { lock (_g) return _tentou && _chave != null; } }

    /// <summary>
    /// Ligado no app de verdade. Desligado em teste automático (<c>STREEMU_TEST_DIR</c>, <c>STREEMU_NOSTATE=1</c>) para os scripts continuarem lendo o log em
    /// texto; <c>STREEMU_CIFRA_LOCAL=1</c> liga de novo e <c>=0</c> desliga. Os testes unitários usam <see cref="Forcar"/>.
    /// </summary>
    public static bool Ligado
    {
        get
        {
            if (_forcado is bool f) return f;
            string? v = Environment.GetEnvironmentVariable("STREEMU_CIFRA_LOCAL");
            if (v == "1") return true;
            if (v == "0") return false;
            return Environment.GetEnvironmentVariable("STREEMU_NOSTATE") != "1" && Environment.GetEnvironmentVariable("STREEMU_TEST_DIR") == null;
        }
    }

    /// <summary>Só os testes: liga (true), desliga (false) ou volta ao padrão (nulo) e esquece a chave em memória.</summary>
    public static void Forcar(bool? ligado) { lock (_g) { _forcado = ligado; _chave = null; _tentou = false; Falha = null; _geracao++; } }

    /// <summary>Só os testes: pasta onde mora o <c>chave-local.dat</c> (padrão: <c>%LOCALAPPDATA%\Streemu</c>). Esquece a chave em memória.</summary>
    public static void UsarPasta(string? pasta) { lock (_g) { _pasta = pasta; _chave = null; _tentou = false; Falha = null; _geracao++; } }

    static string ArquivoDaChave => Path.Combine(_pasta ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Streemu"), "chave-local.dat");

    /// <summary>Quantas aberturas seguidas com a chave existindo e sem abrir bastam para o app guardar a chave como <c>.antigo</c> e criar outra.</summary>
    const int AberturasAteRecriar = 3;
    const int TentativasEmSegundoPlano = 3;
    static TimeSpan _espera = TimeSpan.FromSeconds(10);
    static int _geracao;
    static Thread? _retentativa;

    /// <summary>Só os testes: o intervalo entre as tentativas em segundo plano (nulo volta aos 10 s). Vale para a próxima abertura da chave.</summary>
    public static void EsperaDasTentativas(TimeSpan? espera) { lock (_g) _espera = espera ?? TimeSpan.FromSeconds(10); }

    /// <summary>Só os testes: espera a thread das tentativas em segundo plano terminar (verdadeiro se terminou ou se nem existia).</summary>
    public static bool AguardarTentativas(int milissegundos)
    {
        Thread? t;
        lock (_g) t = _retentativa;
        return t == null || t.Join(milissegundos);
    }

    static string ArquivoDasFalhas(string chave) => Path.Combine(Path.GetDirectoryName(chave)!, "chave-local.falhas");

    static int LerFalhas(string chave)
    {
        try { return int.TryParse(File.ReadAllText(ArquivoDasFalhas(chave)).Trim(), out int n) && n > 0 ? n : 0; }
        catch { return 0; }
    }

    static void GravarFalhas(string chave, int n)
    {
        try
        {
            if (n <= 0) { File.Delete(ArquivoDasFalhas(chave)); return; }
            File.WriteAllText(ArquivoDasFalhas(chave), n.ToString());
        }
        catch { /* sem o contador a chave só deixa de ser recriada; nunca é trocada à toa */ }
    }

    /// <summary>Cria uma chave nova em <paramref name="f"/> (que não existe). Nulo se não deu. Chamar com <c>_g</c> preso.</summary>
    static byte[]? Criar(string f)
    {
        var nova = RandomNumberGenerator.GetBytes(32);
        byte[] protegida = ProtectedData.Protect(nova, Entropia, DataProtectionScope.CurrentUser);
        string tmp = f + "." + Environment.ProcessId + ".tmp";
        File.WriteAllBytes(tmp, protegida);
        try { File.Move(tmp, f, overwrite: false); return nova; }
        catch (IOException)
        {
            // outra instância (testes locais abrem várias) criou a chave um instante antes: vale a dela
            try { File.Delete(tmp); } catch { }
            return Ler(f);
        }
    }

    static byte[]? Chave()
    {
        lock (_g)
        {
            if (_tentou) return _chave;
            _tentou = true;
            try
            {
                string f = ArquivoDaChave;
                Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                _chave = Ler(f);
                if (_chave != null) { if (File.Exists(ArquivoDasFalhas(f))) GravarFalhas(f, 0); }
                // arquivo existe mas não abriu agora (perfil do Windows carregando, outro PC, estragado): nunca troca a chave nesta hora, que levaria a conta e os
                // dados cifrados de antes. Esta abertura começa sem cifra e tenta de novo em segundo plano (a cifra passa a valer assim que abrir).
                else if (File.Exists(f))
                {
                    Falha = "a chave local existe mas não abriu agora; tento de novo sozinho";
                    int geracao = _geracao; var espera = _espera; // lidos agora: a thread só roda depois
                    _retentativa = new Thread(() => TentarDeNovo(f, geracao, espera)) { IsBackground = true, Name = "cofre-local" };
                    _retentativa.Start();
                }
                else _chave = Criar(f);
                if (_chave == null) Falha ??= "não consegui criar a chave local";
            }
            catch (Exception e) { _chave = null; Falha = e.GetType().Name; }
            return _chave;
        }
    }

    /// <summary>
    /// Roda numa thread própria (a da tela e a do log nunca esperam por ela). Tenta abrir a chave algumas vezes: o perfil do Windows pode ainda estar carregando.
    /// Se nenhuma abrir, conta esta abertura como uma falha seguida; na <see cref="AberturasAteRecriar"/>-ésima o arquivo vira <c>.antigo</c> e nasce uma chave nova,
    /// porque um arquivo de outro PC ou estragado nunca vai abrir e a cifra ficaria desligada para sempre. Sem janela e sem pedir nada à pessoa.
    /// </summary>
    static void TentarDeNovo(string f, int geracao, TimeSpan espera)
    {
        try
        {
            for (int i = 0; i < TentativasEmSegundoPlano; i++)
            {
                Thread.Sleep(espera);
                lock (_g)
                {
                    if (geracao != _geracao) return; // os testes trocaram de pasta ou de modo no meio: esta rodada ficou velha
                    var k = Ler(f);
                    if (k == null) continue;
                    _chave = k; Falha = null;
                    GravarFalhas(f, 0);
                    return;
                }
            }
            lock (_g)
            {
                if (geracao != _geracao) return;
                int n = LerFalhas(f) + 1;
                if (n < AberturasAteRecriar) { GravarFalhas(f, n); Falha = $"a chave local não abriu ({n} abertura(s) seguida(s)); sem cifra por enquanto"; return; }
                // outra instância pode ter recriado ou aberto no meio: se abre agora, vale
                if (Ler(f) is { } depois) { _chave = depois; Falha = null; GravarFalhas(f, 0); return; }
                File.Move(f, f + ".antigo", overwrite: true);
                _chave = Criar(f);
                Falha = _chave == null ? "não consegui criar a chave local" : null;
                GravarFalhas(f, 0);
            }
        }
        catch (Exception e) { lock (_g) { if (geracao == _geracao) Falha = e.GetType().Name; } } // nunca lança nem escreve no Log
    }

    static byte[]? Ler(string f)
    {
        try
        {
            if (!File.Exists(f)) return null;
            var k = ProtectedData.Unprotect(File.ReadAllBytes(f), Entropia, DataProtectionScope.CurrentUser);
            return k.Length == 32 ? k : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Cifra um texto numa linha só (sem quebra de linha). Desligado, ou sem chave, devolve o texto como veio (o chamador grava do jeito de antes).
    /// Texto vazio fica vazio.
    /// </summary>
    public static string Linha(string texto)
    {
        if (texto.Length == 0 || !Ligado || Chave() is not { } k) return texto;
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var claro = Encoding.UTF8.GetBytes(texto);
            var cifrado = new byte[claro.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(k, 16)) aes.Encrypt(nonce, claro, cifrado, tag);
            var tudo = new byte[12 + 16 + cifrado.Length];
            nonce.CopyTo(tudo, 0); tag.CopyTo(tudo, 12); cifrado.CopyTo(tudo, 28);
            return Prefixo + Convert.ToBase64String(tudo);
        }
        catch (Exception e) { Falha = e.GetType().Name; return texto; }
    }

    /// <summary>O inverso de <see cref="Linha"/> para UMA linha cifrada. Nulo se não tem o prefixo ou não abre (nunca lança).</summary>
    public static string? Abrir(string linha)
    {
        if (!linha.StartsWith(Prefixo, StringComparison.Ordinal) || Chave() is not { } k) return null;
        try
        {
            var tudo = Convert.FromBase64String(linha[Prefixo.Length..].Trim());
            if (tudo.Length < 28) return null;
            var claro = new byte[tudo.Length - 28];
            using (var aes = new AesGcm(k, 16)) aes.Decrypt(tudo.AsSpan(0, 12), tudo.AsSpan(28), tudo.AsSpan(12, 16), claro);
            return Encoding.UTF8.GetString(claro);
        }
        catch { return null; }
    }

    /// <summary>Texto de uma linha lida do disco: se está cifrada, o conteúdo (ou <see cref="LinhaIlegivel"/>); se é texto antigo, ele mesmo.</summary>
    public static string TextoDaLinha(string linha)
    {
        linha = SemBom(linha);
        return linha.StartsWith(Prefixo, StringComparison.Ordinal) ? Abrir(linha) ?? LinhaIlegivel : linha;
    }

    /// <summary>
    /// O <see cref="Util.Log"/> abre o arquivo com <c>Encoding.UTF8</c>, que grava o BOM (U+FEFF) antes da 1ª linha; <c>GetString</c> não o tira. Sem isto a 1ª
    /// linha do log "não começava" com <c>c1:</c>, passava como texto antigo em claro e o <c>c1:&lt;base64&gt;</c> inteiro subia como conteúdo (teste de campo de 2026-11-26).
    /// </summary>
    static string SemBom(string t) => t.Length > 0 && t[0] == (char)0xFEFF ? t[1..] : t;

    /// <summary>
    /// Cifra cada linha de <paramref name="texto"/> separadamente (o que o arquivo guarda é "uma linha cifrada por linha"). Preserva a quebra no fim, se havia.
    /// Desligado devolve o texto igual.
    /// </summary>
    public static string CifrarLinhas(string texto)
    {
        if (!Ligado || texto.Length == 0) return texto;
        var sb = new StringBuilder(texto.Length + texto.Length / 2);
        int i = 0;
        while (i < texto.Length)
        {
            int j = texto.IndexOf('\n', i);
            string l = j < 0 ? texto[i..] : texto[i..j];
            if (l.EndsWith('\r')) l = l[..^1];
            sb.Append(Linha(l));
            if (j < 0) break;
            sb.Append('\n');
            i = j + 1;
        }
        return sb.ToString();
    }

    /// <summary>Os bytes lidos de um arquivo (linhas inteiras, com a quebra no fim de cada uma) viram o texto em claro que o envio manda. Linha antiga em claro passa como está.</summary>
    public static string DecifrarLinhas(byte[] bytes)
    {
        string texto = SemBom(Encoding.UTF8.GetString(bytes));
        if (!texto.Contains(Prefixo, StringComparison.Ordinal)) return texto;
        var sb = new StringBuilder(texto.Length);
        int i = 0;
        while (i < texto.Length)
        {
            int j = texto.IndexOf('\n', i);
            string l = j < 0 ? texto[i..] : texto[i..j];
            sb.Append(TextoDaLinha(l));
            if (j < 0) break;
            sb.Append('\n');
            i = j + 1;
        }
        return sb.ToString();
    }

    /// <summary>Um arquivo inteiro (JSON de uma vez): o texto cifrado numa linha só. Desligado, devolve o texto.</summary>
    public static string Arquivo(string texto) => Linha(texto);

    /// <summary>Lê o conteúdo de um arquivo gravado por <see cref="Arquivo"/> (ou em claro, de antes da A8).</summary>
    public static string LerArquivo(string caminho)
    {
        string t = File.ReadAllText(caminho);
        string cabeca = t.TrimStart();
        return cabeca.StartsWith(Prefixo, StringComparison.Ordinal) ? Abrir(cabeca.Split('\n')[0].Trim()) ?? "" : t;
    }
}
