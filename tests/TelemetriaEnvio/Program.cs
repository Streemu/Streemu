using System.Text;

// Teste do envio da telemetria (Envio + SessaoLocal) contra um servidor falso em 127.0.0.1. Sem rede real, sem janela, sem o Worker.
//   (sem argumento)  casos de correção
//   --carga          depois dos casos, mede o custo do envio com eventos e log em ritmo normal e 10x (60 s cada)
//   --worker URL     casos contra o Worker de verdade rodando local; --token-conta (token de acesso sa1.* de uma conta de teste, obrigatório: sem conta nada sobe) e --token (leitura, padrão teste-leitura)
//   --sem-casos     (com --carga) pula os casos e só mede a carga
//   --segundos N    duração de cada fase da carga (padrão 60)
//   --saida ARQ      copia o resultado da carga (JSON) para ARQ
//   --sabotar        roda o caso simples contra um servidor que confirma sem gravar: tem que sair com código 1
//   --manter         não apaga a pasta temporária no fim
//   --servidor-falso (interno) sobe só o servidor; a carga o usa como processo filho
Console.OutputEncoding = new UTF8Encoding(false);
if (args.Contains("--servidor-falso")) return ServidorFalso.Principal();

// Antes de tocar em QUALQUER tipo de Streemu.Telemetria: Pasta.Raiz lê STREEMU_TEST_DIR uma vez só. Log.Init nunca é chamado.
string tmp = Path.Combine(Path.GetTempPath(), "streemu-telemetria-envio-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Environment.ProcessId);
Directory.CreateDirectory(tmp);
Environment.SetEnvironmentVariable("STREEMU_TEST_DIR", tmp);
Environment.SetEnvironmentVariable("STREEMU_TELEMETRIA_URL", null);
Casos.Tmp = tmp;

string? Arg(string nome) { int i = Array.IndexOf(args, nome); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

var sw = System.Diagnostics.Stopwatch.StartNew();
if (Arg("--worker") is string workerUrl)
    Casos.RodarWorker(workerUrl, Arg("--token-conta") ?? throw new ArgumentException("--worker precisa de --token-conta (o acesso de uma conta de teste: a senha do grupo acabou)"), Arg("--token") ?? "teste-leitura");
else if (!args.Contains("--sem-casos")) Casos.Rodar(args.Contains("--sabotar"));
Console.WriteLine($"casos: {Casos.Falhas} falha(s) em {sw.Elapsed.TotalSeconds:0.#} s");

if (args.Contains("--carga") && !args.Contains("--sabotar"))
{
    try { Carga.Rodar(tmp, int.TryParse(Arg("--segundos"), out var s) ? s : 60, Arg("--saida")); }
    catch (Exception e) { Casos.Falhas++; Console.WriteLine("FALHA carga: " + e); }
}

if (!args.Contains("--manter") && !args.Contains("--carga"))
    try { Directory.Delete(tmp, true); } catch { }
else Console.WriteLine("pasta temporária mantida: " + tmp);
return Casos.Falhas == 0 ? 0 : 1;
