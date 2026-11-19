-- Interface do filme do Streemu desenhada dentro do mpv (STREEMU_MPV_UI=0 desliga; ver Filme/MpvUi.cs).
--
-- Por que não um ModernX/uosc pronto: os botões deles mexem no mpv direto (seek, pause), e no modo filme isso tira a pessoa de
-- sincronia com o grupo e passa por cima de quem pode controlar. Aqui NENHUM botão muda o ponto do filme: tocar, pausar e pular
-- viram "script-message streemu <ação>" e o app decide (o anfitrião sincroniza com todos; quem assiste pede ao anfitrião). As escolhas
-- do anfitrião (todos controlam, transmitir, esperar todos) e o "sair da sessão" também só pedem ao app.
-- O que é só desta tela vai direto para o mpv: volume, mudo, preencher a tela e as faixas de legenda, áudio e vídeo (a de áudio do
-- anfitrião já é a que o filme pela transmissão manda, então trocar aqui troca para quem assiste sem o arquivo).
--
-- A posição e a duração vêm do app ("streemu-estado", várias vezes por segundo): no filme pela transmissão (spec 011) a duração do fluxo
-- é desconhecida e o mpv pode estar esperando dados; só o app sabe onde o filme do grupo está. "buffer" ({{início, fim}, ...} em s)
-- é o que o mpv de quem assiste já tem guardado. As cores também vêm do app ("tema", do Theme.cs): a paleta é uma só para o app e o player.
--
-- Desenho: mockups "Cinema Participante" e "Cinema Anfitrião" do grupo (2026-11-15).

local mp = require 'mp'
local utils = require 'mp.utils'

-- cores em BGR (formato do ASS); o app manda as do Theme.cs em "tema" e estas só valem até a primeira mensagem
local COR = { acento = 'DC627C', acentoClaro = 'ED768F', acentoTexto = 'FFFFFF', texto = 'FAF0F4', apagado = 'DBBAC5', painel = '2E1B1E', borda = '63444B',
    ok = '6ADC3D', espera = '32B2F0', erro = '4D48E5', preto = '000000', branco = 'FFFFFF', avatar = 'F4A8B9' }
local FONTE, ICONES = 'Segoe UI', 'Segoe MDL2 Assets'
-- Segoe MDL2 Assets (vem no Windows 10 e 11), conferidos numa captura: tela cheia E740, sair dela E73F, volume E767, mudo E74F,
-- legenda E7F0, áudio E8D6, vídeo E714, marcado E73E, sair da sessão E89F, relógio E823, preencher E9A6, engrenagem E713,
-- episódio anterior E892, próximo E893, lista (fila) E8FD
local G = { cheia = '\238\157\128', sair_cheia = '\238\156\191', volume = '\238\157\167', mudo = '\238\157\143',
    sub = '\238\159\176', audio = '\238\163\150', video = '\238\156\148', ok = '\238\156\190',
    anterior = '\238\162\146', proximo = '\238\162\147', fila = '\238\163\189',
    sair = '\238\162\159', relogio = '\238\160\163', preencher = '\238\166\166', engrenagem = '\238\156\147' }
local NOME_MENU = { sub = 'Legenda', audio = 'Áudio', video = 'Vídeo' }
local IDIOMA = { por = 'Português', pt = 'Português', pob = 'Português', ['pt-br'] = 'Português', eng = 'Inglês', en = 'Inglês',
    jpn = 'Japonês', ja = 'Japonês', spa = 'Espanhol', es = 'Espanhol', fre = 'Francês', fra = 'Francês', fr = 'Francês',
    ger = 'Alemão', deu = 'Alemão', de = 'Alemão', ita = 'Italiano', it = 'Italiano', kor = 'Coreano', ko = 'Coreano',
    chi = 'Chinês', zho = 'Chinês', zh = 'Chinês', rus = 'Russo', ru = 'Russo', ara = 'Árabe', ar = 'Árabe' }
local PASSO = 5 -- −5/+5: com 10 s o pulo passava do ponto (pedido do 2º teste)
-- "Opções da sessão" do anfitrião: a ação que o app recebe, o texto e o campo do estado que diz se está ligada
local OPCOES = { { 'todos', 'Todos controlam', 'todos' }, { 'transmitir', 'Permitir assistir sem baixar', 'transmitir' },
    { 'esperar', 'Esperar todos para começar', 'esperar' } }

local est = { pos = 0, at = mp.get_time(), dur = nil, tocando = false, pode = true, titulo = '', linha = '', estado = '', estadoTom = '',
    cheia = false, pessoas = {}, buffer = {}, anfitriao = false, aguardando = false, todos = false, transmitir = false, esperar = false,
    esperarVisivel = false, fila = nil }
local ov = mp.create_osd_overlay('ass-events')
local mouse = { x = -1, y = -1, dentro = false }
local houveHover = false
local ultimoMov = -100
local arrastando, arrastoPos = nil, 0
local botoes = {}
local menu, menuIni = nil, 0 -- 'sub', 'audio', 'video' ou 'sessao'; menuIni = primeira linha mostrada (roda do mouse)
local faixas = {}
local fixo = false        -- teste: controles sempre na tela
local alturaTela = nil    -- teste: finge a altura do monitor (720, 1080, 1440, 2160)
local janelaTeste = nil   -- teste: desenha como numa janela desse tamanho ({w, h}), em miniatura na janela de verdade (4K e ultrawide sem ter o monitor)
local avisouPronto = false
local mostrava = false
local cliques = 0
local autoEsc = nil
local linhas = {}

local unpack = unpack or table.unpack
local function n(v) return math.floor(v + 0.5) end
local function limitar(v, a, b) if v < a then return a elseif v > b then return b end return v end
local function esc(s) return (tostring(s or ''):gsub('\\', '\\\239\187\160'):gsub('{', '\\{'):gsub('\n', ' ')) end

local function caracteres(s)
    local t = {}
    for ch in tostring(s or ''):gmatch('[%z\1-\127\194-\244][\128-\191]*') do t[#t + 1] = ch end
    return t
end

-- corta em caracteres (UTF-8), não em bytes: nome de arquivo de anime tem japonês
local function cortar(s, max)
    local t = caracteres(s)
    if #t <= max then return table.concat(t) end
    local r = {}
    for i = 1, math.max(1, max - 1) do r[i] = t[i] end
    return table.concat(r) .. '…'
end

-- largura aproximada de um texto em Segoe UI (o ASS não mede): ~0,43 do tamanho por caractere (medido nas capturas do player)
local function largura(s, tam) return #caracteres(s) * tam * 0.43 end

local function tempo(s)
    if not s or s ~= s or s < 0 then s = 0 end
    s = math.floor(s)
    local h, m, ss = math.floor(s / 3600), math.floor(s % 3600 / 60), s % 60
    if h > 0 then return string.format('%d:%02d:%02d', h, m, ss) end
    return string.format('%d:%02d', m, ss)
end

local function duracao()
    if est.dur and est.dur > 0 then return est.dur end
    local d = mp.get_property_number('duration')
    if d and d > 0 then return d end
    return nil
end

local function posicao()
    local p = est.pos
    if est.tocando then p = p + (mp.get_time() - est.at) end
    local d = duracao()
    if d and p > d then p = d end
    return p
end

local function manda(...) mp.commandv('script-message', 'streemu', ...) end

-- ------------------------------------------------------------------ tamanho

-- O tamanho segue a RESOLUÇÃO DO MONITOR, não a janela: 1080p = 1; 720p ~0,75 (ainda legível); 1440p ~1,22; 4K ~1,62. A potência 0,7
-- cresce menos que a resolução, porque num monitor maior a pessoa senta mais longe mas não tanto. Numa janela pequena num monitor
-- grande, encolhe só o bastante para caber: a barra larga perde os textos dos botões antes (ver "compacto"), então ~820 de largura
-- e ~460 de altura bastam no tamanho 1.
local function escala(w, h)
    local dh = alturaTela or mp.get_property_number('display-height') or h
    local s = (dh / 1080) ^ 0.7
    return limitar(math.min(s, (w - 16) / 820, h / 460), 0.5, 2.4)
end

-- ------------------------------------------------------------------ faixas

local function tipo(t) return t.type == 'sub' and 'sub' or t.type == 'audio' and 'audio' or (t.type == 'video' and not t.albumart) and 'video' or nil end

local function faixasDe(qual)
    local r = {}
    for _, t in ipairs(faixas) do if tipo(t) == qual then r[#r + 1] = t end end
    return r
end

-- legenda aparece com 1 faixa (dá para ligar e desligar); áudio e vídeo só com 2 ou mais (com uma, não há o que escolher)
local function temMenu(qual)
    if qual == 'sessao' then return est.anfitriao end
    local c = #faixasDe(qual); return qual == 'sub' and c >= 1 or c >= 2
end

local function nomeFaixa(t)
    local lang = t.lang and IDIOMA[t.lang:lower()] or (t.lang and t.lang ~= 'und' and t.lang:upper()) or nil
    local nome = lang
    if t.title and t.title ~= '' and t.title ~= t.lang then nome = nome and (nome .. ' · ' .. t.title) or t.title end
    nome = nome or ('Faixa ' .. t.id)
    if t.type == 'audio' then
        local ch = t['demux-channel-count']
        local tx = ch == 1 and 'mono' or ch == 2 and '2.0' or ch == 6 and '5.1' or ch == 8 and '7.1' or nil
        if tx then nome = nome .. ' · ' .. tx end
    end
    if t.forced then nome = nome .. ' (forçada)' end
    if t.external then nome = nome .. ' (arquivo)' end
    return nome
end

local function itensMenu(qual)
    local itens = {}
    if qual == 'sub' then
        local alguma = false
        for _, t in ipairs(faixasDe('sub')) do if t.selected then alguma = true end end
        itens[1] = { nome = 'Desligada', valor = 'no', sel = not alguma }
    end
    for _, t in ipairs(faixasDe(qual)) do itens[#itens + 1] = { nome = nomeFaixa(t), valor = tostring(t.id), sel = t.selected == true } end
    return itens
end

-- Preencher a tela só aparece quando o vídeo não tem o formato da janela (ex.: 16:9 num monitor ultrawide 21:9, ou um filme 2,39:1
-- num 16:9). Ligado, corta as bordas (panscan) em vez de deixar as faixas pretas; é só desta tela.
local function aspectoDiferente(w, h)
    local va = mp.get_property_number('video-params/aspect')
    if not va or va <= 0 then return false end
    return math.abs(va / (w / h) - 1) > 0.03 or (mp.get_property_number('panscan') or 0) > 0
end

-- ------------------------------------------------------------------ desenho (ASS)

local function add(s) linhas[#linhas + 1] = s end

local function forma(cmds, cor, alfa, borda, corBorda)
    add(string.format('{\\an7\\pos(0,0)\\bord%s\\shad0\\1c&H%s&\\1a&H%s&\\3c&H%s&\\3a&H00&\\p1}%s{\\p0}',
        borda or 0, cor, alfa or '00', corBorda or cor, cmds))
end

local function ret(x, y, w, h, cor, alfa)
    x, y, w, h = n(x), n(y), n(w), n(h)
    forma(string.format('m %d %d l %d %d %d %d %d %d', x, y, x + w, y, x + w, y + h, x, y + h), cor, alfa)
end

local function circulo(cx, cy, r, cor, alfa)
    local k = r * 0.5523
    local p = { cx - r, cy, cx - r, cy - k, cx - k, cy - r, cx, cy - r, cx + k, cy - r, cx + r, cy - k, cx + r, cy,
        cx + r, cy + k, cx + k, cy + r, cx, cy + r, cx - k, cy + r, cx - r, cy + k, cx - r, cy }
    for i = 1, #p do p[i] = n(p[i]) end
    forma(string.format('m %d %d b %d %d %d %d %d %d b %d %d %d %d %d %d b %d %d %d %d %d %d b %d %d %d %d %d %d', unpack(p)), cor, alfa)
end

-- retângulo de pontas arredondadas com raio r (r = h/2 vira pílula); com corBorda, ganha a borda fina dos cartões do app
local function arred(x, y, w, h, r, cor, alfa, corBorda, espessura)
    r = math.min(r, h / 2, w / 2)
    local k = r * 0.5523
    local p = { x + r, y, x + w - r, y, x + w - r + k, y, x + w, y + r - k, x + w, y + r, x + w, y + h - r,
        x + w, y + h - r + k, x + w - r + k, y + h, x + w - r, y + h, x + r, y + h, x + r - k, y + h, x, y + h - r + k, x, y + h - r,
        x, y + r, x, y + r - k, x + r - k, y, x + r, y }
    for i = 1, #p do p[i] = n(p[i]) end
    local cmds = string.format('m %d %d l %d %d b %d %d %d %d %d %d l %d %d b %d %d %d %d %d %d l %d %d b %d %d %d %d %d %d l %d %d b %d %d %d %d %d %d', unpack(p))
    if corBorda then forma(cmds, cor, alfa, string.format('%.1f', espessura or 1), corBorda) else forma(cmds, cor, alfa) end
end
local function pilula(x, y, w, h, cor, alfa) arred(x, y, w, h, h / 2, cor, alfa) end

-- de y0 a y1, transparente numa ponta e com opacidade maxima (0-255) na outra (em cima se invertido).
-- Faixas de borda inteira: sobrepostas, a emenda vira listra.
local function degrade(y0, y1, maxima, invertido, w)
    local nf = 32
    for i = 0, nf - 1 do
        local a, b = n(y0 + (y1 - y0) * i / nf), n(y0 + (y1 - y0) * (i + 1) / nf)
        local t = (i + 0.5) / nf
        if invertido then t = 1 - t end
        if b > a then ret(0, a, w, b - a, COR.preto, string.format('%02X', n(255 - maxima * t * t))) end
    end
end

local function texto(x, y, an, tam, s, cor, alfa, fonte, negrito)
    add(string.format('{\\an%d\\q2\\pos(%d,%d)\\bord0\\shad1\\4c&H000000&\\4a&HA0&\\fn%s\\fs%d\\b%d\\1c&H%s&\\1a&H%s&}%s',
        an, n(x), n(y), fonte or FONTE, n(tam), negrito and 1 or 0, cor, alfa or '00', s))
end

local function sobre(b) return mouse.dentro and mouse.x >= b.x and mouse.x <= b.x + b.w and mouse.y >= b.y and mouse.y <= b.y + b.h end

local function botao(id, x, y, w, h, valor)
    local b = { id = id, x = x, y = y, w = w, h = h, valor = valor }
    botoes[#botoes + 1] = b
    return b, sobre(b)
end

-- "aviso" (recado curto do app, como "Só Lucas controla") é amarelo como a espera, mas sem o relógio
local function corDoTom(t) return t == 'ok' and COR.ok or (t == 'espera' or t == 'aviso') and COR.espera or t == 'erro' and COR.erro or COR.texto end

-- o que aparece escrito com o mouse em cima de cada botão
local function dica(b)
    local id = b.id
    if id == 'tocar' then return not est.pode and 'Só o anfitrião controla' or est.aguardando and 'Começar sem esperar os outros'
        or (est.tocando and 'Pausar' or 'Tocar') .. ' (espaço ou botão direito)'
    elseif id == 'voltar' then return 'Voltar ' .. PASSO .. ' s (←)'
    elseif id == 'avancar' then return 'Avançar ' .. PASSO .. ' s (→)'
    elseif id == 'cheia' then return est.cheia and 'Sair da tela cheia (Esc)' or 'Tela cheia (F)'
    elseif id == 'mudo' then return mp.get_property_bool('mute') and 'Ligar o som (M)' or 'Tirar o som (M)'
    elseif id == 'volume' then return string.format('Volume %d%%', n(mp.get_property_number('volume') or 100))
    elseif id == 'menu' then return NOME_MENU[b.valor]
    elseif id == 'fila' then return 'Fila de reprodução (L)'
    elseif id == 'anterior' then return 'Episódio anterior'
    elseif id == 'proximo' then return 'Próximo episódio'
    elseif id == 'preencher' then return (mp.get_property_number('panscan') or 0) > 0 and 'Mostrar o vídeo inteiro' or 'Preencher a tela (corta as bordas)'
    elseif id == 'sair' then return est.anfitriao and 'Encerrar a sessão para todos' or 'Sair da sessão'
    end
    return nil
end

-- Pausado, os controles também somem, só que depois de 4 s em vez de 2 (pedido de 2026-11-16: a barra parada no filme pausado tirava
-- a imersão). Menu aberto ou arrasto seguram a barra; o mouse parado em cima do painel também. Espera ("Esperando Bruno"), aviso e o anfitrião
-- aguardando para começar também: são coisas que a pessoa precisa ver, não uma pausa comum.
local function visivel()
    if fixo or arrastando or menu or est.aguardando or est.estadoTom == 'espera' or est.estadoTom == 'aviso' then return true end
    if mouse.dentro and mouse.y > select(2, mp.get_osd_size()) * 0.75 then return true end -- mouse em cima do painel
    return mp.get_time() - ultimoMov < (est.tocando and 2 or 4)
end

local function desenharMenu(s, w, xDir, yBase)
    local itens = itensMenu(menu)
    local lh, pw = 34 * s, 330 * s
    local cab = 40 * s
    local maxLinhas = math.max(3, math.floor((yBase - 40 * s - cab) / lh))
    menuIni = limitar(menuIni, 0, math.max(0, #itens - maxLinhas))
    local vis = math.min(#itens, maxLinhas)
    local ph = cab + vis * lh + 10 * s
    local px = limitar(xDir - pw, 12 * s, w - pw - 12 * s)
    local py = yBase - ph
    botao('painel', px, py, pw, ph)
    arred(px, py, pw, ph, 12 * s, COR.painel, '10', COR.borda)
    texto(px + 18 * s, py + cab / 2 + 2 * s, 4, 18 * s, NOME_MENU[menu] .. (#itens > vis and string.format('  (%d de %d)', menuIni + 1, #itens) or ''), COR.texto, '00', FONTE, true)
    local maxCar = math.floor((pw - 60 * s) / (16 * s * 0.5))
    for i = 1, vis do
        local it = itens[i + menuIni]
        local y = py + cab + (i - 1) * lh
        local _, hv = botao('item', px + 4 * s, y, pw - 8 * s, lh, it.valor)
        if hv then arred(px + 4 * s, y, pw - 8 * s, lh, 7 * s, COR.branco, 'E2') end
        if it.sel then texto(px + 18 * s, y + lh / 2, 4, 16 * s, G.ok, COR.acento, '00', ICONES) end
        texto(px + 46 * s, y + lh / 2, 4, 16 * s, esc(cortar(it.nome, maxCar)), COR.texto, it.sel and '00' or '30', FONTE, it.sel)
    end
end

-- interruptor como o ToggleSwitch do app: ligado = trilho na cor de destaque com a bolinha à direita
local function interruptor(x, cy, s, ligado)
    local tw, th = 50 * s, 26 * s
    pilula(x, cy - th / 2, tw, th, ligado and COR.acento or COR.apagado, ligado and '00' or '90')
    circulo(ligado and x + tw - th / 2 or x + th / 2, cy, th / 2 - 3 * s, COR.branco)
end

-- "Opções da sessão" do anfitrião, em cima do botão Sessão (a ponta aponta para ele)
local function desenharSessao(s, w, ancora, yBase)
    local itens = {}
    for _, o in ipairs(OPCOES) do if o[1] ~= 'esperar' or est.esperarVisivel then itens[#itens + 1] = o end end
    local lh, pw, cab = 44 * s, 400 * s, 52 * s
    local ph = cab + #itens * lh + 12 * s
    local px = limitar(ancora - pw * 0.33, 12 * s, w - pw - 12 * s)
    local py = yBase - ph
    botao('painel', px, py, pw, ph)
    arred(px, py, pw, ph, 12 * s, COR.painel, '08', COR.borda)
    local pa = 10 * s -- a ponta embaixo, no meio do botão
    forma(string.format('m %d %d l %d %d %d %d', n(ancora - pa), n(py + ph - 1), n(ancora + pa), n(py + ph - 1), n(ancora), n(py + ph + pa - 1)), COR.painel, '08')
    texto(px + 22 * s, py + cab / 2 + 2 * s, 4, 21 * s, 'Opções da sessão', COR.texto, '00', FONTE, true)
    for i, o in ipairs(itens) do
        local y = py + cab + (i - 1) * lh
        local _, hv = botao('opcao', px + 6 * s, y, pw - 12 * s, lh, o[1])
        if hv then arred(px + 6 * s, y, pw - 12 * s, lh, 8 * s, COR.branco, 'EA') end
        texto(px + 22 * s, y + lh / 2, 4, 18 * s, o[2], COR.texto, '00')
        interruptor(px + pw - 22 * s - 50 * s, y + lh / 2, s, est[o[3]])
    end
end

-- ------------------------------------------------------------------ em cima: título, papel, estado e quem está na sessão

local function desenharPessoas(s, w, mx)
    if #est.pessoas == 0 then return 0 end
    local pw, cab, lh = 360 * s, 52 * s, 60 * s
    local ph = cab + #est.pessoas * lh + 8 * s
    local px, py = w - mx - pw, 20 * s
    arred(px, py, pw, ph, 12 * s, COR.painel, '1C', COR.borda)
    texto(px + 20 * s, py + cab / 2 + 4 * s, 4, 22 * s, 'Na sessão', COR.texto, '00', FONTE, true)
    for i, p in ipairs(est.pessoas) do
        local cy = py + cab + (i - 1) * lh + lh / 2
        local r = 21 * s
        circulo(px + 20 * s + r, cy, r, p.c or COR.avatar)
        texto(px + 20 * s + r, cy, 5, r * 0.82, esc(p.i or '?'), COR.branco, '00', FONTE, true)
        local tx = px + 20 * s + 2 * r + 14 * s
        local direita = (not p.junto and p.e and p.e ~= '') and largura(p.e, 18 * s) + 12 * s or 0
        local maxCar = math.max(4, math.floor((px + pw - 18 * s - direita - tx) / (19 * s * 0.52)))
        if p.p and p.p ~= '' then
            texto(tx, cy - 11 * s, 4, 19 * s, esc(cortar(p.n, maxCar)), COR.texto)
            texto(tx, cy + 13 * s, 4, 16 * s, esc(p.p), COR.apagado)
            if p.junto and p.e and p.e ~= '' then texto(tx + largura(p.p, 16 * s), cy + 13 * s, 4, 16 * s, esc(' · ' .. p.e), corDoTom(p.t)) end
        else
            texto(tx, cy, 4, 19 * s, esc(cortar(p.n, maxCar)), COR.texto)
        end
        if direita > 0 then texto(px + pw - 18 * s, cy, 6, 18 * s, esc(p.e), corDoTom(p.t)) end
    end
    return pw
end

local function desenharTitulo(s, w, mx, pwPessoas)
    if est.titulo == '' and est.linha == '' and est.estado == '' then return end
    degrade(0, 150 * s, 170, true, w)
    local larg = w - 2 * mx - (pwPessoas > 0 and pwPessoas + 24 * s or 0)
    texto(mx, 24 * s, 7, 35 * s, esc(cortar(est.titulo, math.floor(larg / (35 * s * 0.55)))), COR.texto, '00', FONTE, true)
    local y = 24 * s + 52 * s
    if est.linha ~= '' then texto(mx, y, 7, 23 * s, esc(cortar(est.linha, math.floor(larg / (23 * s * 0.52)))), COR.texto, '10'); y = y + 38 * s end
    if est.estado == '' then return end
    local cor, x = corDoTom(est.estadoTom), mx
    if est.estadoTom == 'espera' then texto(x, y + 14 * s, 4, 23 * s, G.relogio, cor, '00', ICONES); x = x + 32 * s end
    texto(x, y, 7, 22 * s, esc(cortar(est.estado, math.floor((larg - (x - mx)) / (22 * s * 0.52)))), cor, est.estadoTom == '' and '10' or '00')
end

-- ------------------------------------------------------------------ a barra de baixo
-- Uma fila: à esquerda sair (e Sessão do anfitrião), o tempo, −5, tocar, +5 e a duração; à direita, só desta tela (faixas, preencher,
-- volume, tela cheia). Separadores finos entre os grupos, como no desenho. Sem espaço, os botões perdem o texto ("compacto").

local function separador(s, cy) return { w = 1, sep = true, desenha = function(x) ret(x, cy - 15 * s, math.max(1, n(s)), 30 * s, COR.texto, 'C8') end } end

-- ícone com texto opcional ao lado; "cor" pinta os dois (vermelho no "Encerrar sessão")
local function elIcone(s, cy, id, glifo, rotulo, opc)
    opc = opc or {}
    local tb, tam = 42 * s, 19 * s
    local w = tb + (rotulo and largura(rotulo, tam) + 8 * s or 0) + (opc.fundo and 14 * s or 0)
    return { w = w, desenha = function(x)
        local _, hv = botao(id, x, cy - 21 * s, w, 42 * s, opc.valor)
        if opc.fundo and (hv or opc.aceso) then arred(x, cy - 20 * s, w, 40 * s, 8 * s, COR.branco, opc.aceso and 'D8' or 'E4') end
        local cor = opc.cor or ((hv or opc.ligado) and COR.acento or COR.texto)
        if opc.cor and hv then cor = opc.cor end
        local x0 = x + (opc.fundo and 7 * s or 0)
        texto(x0 + tb / 2, cy, 5, 21 * s, glifo, cor, opc.apagado and '90' or '00', ICONES)
        if rotulo then texto(x0 + tb, cy, 4, tam, rotulo, opc.cor or COR.texto, '00') end
    end }
end

local function elTexto(s, cy, str, alfa)
    return { w = largura(str, 19 * s), desenha = function(x) texto(x, cy, 4, 19 * s, str, COR.texto, alfa or '00') end }
end

local function elPulo(s, cy, id, rotulo, podeA)
    return { w = 40 * s, desenha = function(x)
        local _, hv = botao(id, x, cy - 20 * s, 40 * s, 40 * s)
        texto(x + 20 * s, cy, 5, 19 * s, rotulo, (hv and est.pode) and COR.acento or COR.texto, podeA, FONTE, true)
    end }
end

-- tocar/pausar num quadrado na cor de destaque; "Começar agora" (anfitrião esperando os outros) vira uma pílula com o texto
local function elTocar(s, cy, podeA, compacto)
    local rotulo = est.aguardando and not compacto and 'Começar agora' or nil
    local w = rotulo and (44 * s + largura(rotulo, 19 * s) + 14 * s) or 44 * s
    return { w = w, desenha = function(x)
        local _, hv = botao('tocar', x, cy - 21 * s, w, 42 * s)
        arred(x, cy - 20 * s, w, 40 * s, 8 * s, hv and est.pode and COR.acentoClaro or COR.acento, podeA)
        local cx, r, c = x + 22 * s, 9 * s, COR.acentoTexto
        if est.tocando then
            ret(cx - r * 0.75, cy - r, r * 0.5, r * 2, c, podeA)
            ret(cx + r * 0.25, cy - r, r * 0.5, r * 2, c, podeA)
        else
            forma(string.format('m %d %d l %d %d %d %d', n(cx - r * 0.55), n(cy - r), n(cx + r * 0.95), n(cy), n(cx - r * 0.55), n(cy + r)), c, podeA)
        end
        if rotulo then texto(x + 40 * s, cy, 4, 19 * s, rotulo, c, podeA) end
    end }
end

local function elVolume(s, cy, vw)
    return { w = vw, desenha = function(x)
        local _, hv = botao('volume', x, cy - 12 * s, vw, 24 * s)
        local vol = limitar((mp.get_property_number('volume') or 100) / 100, 0, 1)
        local mudo = mp.get_property_bool('mute')
        pilula(x, cy - 2.5 * s, vw, 5 * s, COR.branco, 'B4')
        if vol > 0 and not mudo then pilula(x, cy - 2.5 * s, vw * vol, 5 * s, COR.acento) end
        circulo(x + vw * vol, cy, (hv or arrastando == 'volume') and 9 * s or 8 * s, COR.branco)
    end }
end

local function filaEsquerda(s, cy, d, p, compacto, podeA)
    local f = {}
    f[#f + 1] = elIcone(s, cy, 'sair', G.sair, not compacto and (est.anfitriao and 'Encerrar sessão' or 'Sair da sessão') or nil,
        { cor = est.anfitriao and COR.erro or nil })
    f[#f + 1] = separador(s, cy)
    if est.anfitriao then
        f[#f + 1] = elIcone(s, cy, 'menu', G.engrenagem, not compacto and 'Sessão' or nil, { fundo = true, aceso = menu == 'sessao', valor = 'sessao' })
        f[#f + 1] = separador(s, cy)
    end
    -- fila de episódios: ⏮ e ⏭ só para o anfitrião e só com mais de um item; apagados quando não há para onde ir
    local fila = est.anfitriao and est.fila and (tonumber(est.fila.total) or 0) > 1
    f[#f + 1] = elTexto(s, cy, tempo(p))
    if fila then f[#f + 1] = elIcone(s, cy, 'anterior', G.anterior, nil, { apagado = not est.fila.temAnterior }) end
    f[#f + 1] = elPulo(s, cy, 'voltar', '−' .. PASSO, podeA)
    f[#f + 1] = elTocar(s, cy, podeA, compacto)
    f[#f + 1] = elPulo(s, cy, 'avancar', '+' .. PASSO, podeA)
    if fila then f[#f + 1] = elIcone(s, cy, 'proximo', G.proximo, nil, { apagado = not est.fila.temProximo }) end
    if compacto ~= 2 then f[#f + 1] = elTexto(s, cy, d and tempo(d) or '--:--', '30') end
    return f
end

-- da direita para a esquerda: tela cheia, volume, mudo | preencher | vídeo, áudio, legenda (na tela: legenda, áudio, vídeo)
local function filaDireita(s, cy, w, h, compacto)
    local f = {}
    f[#f + 1] = elIcone(s, cy, 'cheia', est.cheia and G.sair_cheia or G.cheia)
    f[#f + 1] = elVolume(s, cy, (compacto == 2 and 64 or 120) * s)
    local mudo = mp.get_property_bool('mute') or (mp.get_property_number('volume') or 100) == 0
    f[#f + 1] = elIcone(s, cy, 'mudo', mudo and G.mudo or G.volume)
    -- a fila: o anfitrião sempre vê (é por ela que acrescenta episódios); quem assiste só quando o anfitrião tem uma
    if est.anfitriao or (est.fila and (tonumber(est.fila.total) or 0) > 0) then
        f[#f + 1] = separador(s, cy)
        f[#f + 1] = elIcone(s, cy, 'fila', G.fila, not compacto and 'Fila' or nil, { ligado = est.fila ~= nil and est.fila.aberta == true })
    end
    if aspectoDiferente(w, h) then
        f[#f + 1] = separador(s, cy)
        f[#f + 1] = elIcone(s, cy, 'preencher', G.preencher, not compacto and 'Preencher' or nil, { ligado = (mp.get_property_number('panscan') or 0) > 0 })
    end
    local faixasF = {}
    for _, qual in ipairs({ 'video', 'audio', 'sub' }) do
        if temMenu(qual) then faixasF[#faixasF + 1] = elIcone(s, cy, 'menu', G[qual], nil, { ligado = menu == qual, valor = qual }) end
    end
    if #faixasF > 0 then f[#f + 1] = separador(s, cy) end
    for _, e in ipairs(faixasF) do f[#f + 1] = e end
    return f
end

local function larguraFila(f, gap) local t = 0; for i, e in ipairs(f) do t = t + e.w + (i > 1 and gap or 0) end; return t end

local function desenharBarra(s, w, h)
    local mx = 22 * s
    local pw, ph = w - 2 * mx, 112 * s
    local x0, py = mx, h - mx - ph
    arred(x0, py, pw, ph, 14 * s, COR.painel, '1C', COR.borda)

    -- linha do tempo: o progresso vale para quem não controla também; a bolinha fica sempre, como no desenho
    local d = duracao()
    local lx, lw, ly = x0 + 26 * s, pw - 52 * s, py + 30 * s
    local _, hovL = botao('linha', lx, ly - 12 * s, lw, 24 * s)
    local p = arrastando == 'linha' and arrastoPos or posicao()
    local frac = d and limitar(p / d, 0, 1) or 0
    local grossa = (hovL or arrastando == 'linha') and est.pode and not menu
    local esp = (grossa and 7 or 5) * s
    pilula(lx, ly - esp / 2, lw, esp, COR.branco, 'B4')
    -- filme pela transmissão: o que já chegou e está guardado no player, como a faixa cinza do YouTube (pular dentro dela não recarrega)
    if d and est.buffer then
        for _, f in ipairs(est.buffer) do
            local a, b = limitar((tonumber(f[1]) or 0) / d, 0, 1), limitar((tonumber(f[2]) or 0) / d, 0, 1)
            if b > a then pilula(lx + lw * a, ly - esp / 2, lw * (b - a), esp, COR.branco, '78') end
        end
    end
    if frac > 0 then pilula(lx, ly - esp / 2, lw * frac, esp, COR.acento) end
    circulo(lx + lw * frac, ly, (grossa and 10 or 9) * s, COR.branco)
    if grossa and d then
        local tf = arrastando == 'linha' and frac or limitar((mouse.x - lx) / lw, 0, 1)
        local tx = limitar(lx + lw * tf, lx + 34 * s, lx + lw - 34 * s)
        pilula(tx - 34 * s, ly - 44 * s, 68 * s, 26 * s, '1E1E1E', '20')
        texto(tx, ly - 31 * s, 5, 16 * s, tempo(tf * d), COR.texto)
    end

    local cy = py + ph - 36 * s
    local podeA = est.pode and '00' or '90' -- botões de quem não controla ficam apagados (o app ainda responde com o aviso)
    local gap, livre = 14 * s, pw - 32 * s
    local esq, dir, compacto
    for _, c in ipairs({ false, 1, 2 }) do
        compacto = c
        esq, dir = filaEsquerda(s, cy, d, p, c, podeA), filaDireita(s, cy, w, h, c)
        if larguraFila(esq, gap) + larguraFila(dir, gap) + 40 * s <= livre then break end
    end
    local x, ancora = x0 + 16 * s, nil
    for _, e in ipairs(esq) do
        e.desenha(x)
        if menu == 'sessao' and not ancora and #botoes > 0 and botoes[#botoes].valor == 'sessao' then ancora = x + e.w / 2 end
        x = x + e.w + gap
    end
    local xr, menuX = x0 + pw - 16 * s, nil
    for _, e in ipairs(dir) do
        xr = xr - e.w
        e.desenha(xr)
        local b = botoes[#botoes]
        if b and b.id == 'menu' and b.valor == menu then menuX = xr + e.w end
        xr = xr - gap
    end
    if menu == 'sessao' and ancora then desenharSessao(s, w, ancora, py - 12 * s)
    elseif menu and menuX then desenharMenu(s, w, menuX, py - 10 * s) end
    return py
end

local function desenharDica(s, w, py)
    if menu or arrastando then return end
    for i = #botoes, 1, -1 do
        local b = botoes[i]
        local txt = sobre(b) and dica(b)
        if txt then
            local lw2 = largura(txt, 15 * s) + 30 * s
            local bx = limitar(b.x + b.w / 2, lw2 / 2 + 8 * s, w - lw2 / 2 - 8 * s)
            pilula(bx - lw2 / 2, py - 40 * s, lw2, 30 * s, '1E1E1E', '18')
            texto(bx, py - 25 * s, 5, 15 * s, esc(txt), COR.texto)
            return
        end
        if txt == nil and sobre(b) then return end
    end
end

local function desenhar()
    local w, h = mp.get_osd_size()
    if janelaTeste then w, h = janelaTeste[1], janelaTeste[2] end
    if not w or w <= 0 or h <= 0 then return end
    linhas, botoes = {}, {}
    if menu and not temMenu(menu) then menu = nil end
    if not visivel() then
        if mostrava then ov.data = ''; ov:update(); mostrava = false end
        return
    end
    mostrava = true
    local s = escala(w, h)
    local mx = 30 * s

    degrade(h - 200 * s, h, 190, false, w)
    local pwPessoas = desenharPessoas(s, w, 26 * s)
    desenharTitulo(s, w, mx, pwPessoas)
    local py = desenharBarra(s, w, h)
    desenharDica(s, w, py)

    -- o cursor some sozinho (cursor-autohide), mas não com ele em cima do painel ou de um menu aberto
    local querEsc = (menu or (mouse.dentro and mouse.y > py - 10 * s)) and 'no' or '1500'
    if querEsc ~= autoEsc then autoEsc = querEsc; mp.set_property('cursor-autohide', querEsc) end

    ov.res_x, ov.res_y = w, h
    ov.data = table.concat(linhas, '\n')
    ov:update()
end

-- ------------------------------------------------------------------ entrada

local function acharBotao()
    for i = #botoes, 1, -1 do if sobre(botoes[i]) then return botoes[i] end end
    return nil
end

local function arrastar()
    for _, b in ipairs(botoes) do
        if b.id == arrastando then
            local f = limitar((mouse.x - b.x) / b.w, 0, 1)
            if arrastando == 'linha' then arrastoPos = f * (duracao() or 0)
            else mp.set_property_number('volume', n(f * 100)); if f > 0 then mp.set_property_bool('mute', false) end end
        end
    end
end

local function acao(b)
    local id = b.id
    if id == 'tocar' then manda('tocar')
    elseif id == 'voltar' then manda('pular', tostring(-PASSO))
    elseif id == 'avancar' then manda('pular', tostring(PASSO))
    elseif id == 'cheia' or id == 'sair' or id == 'fila' or id == 'anterior' or id == 'proximo' then manda(id)
    elseif id == 'opcao' then manda(b.valor) -- o menu da sessão fica aberto: o interruptor muda quando o estado do app chegar
    elseif id == 'mudo' then mp.commandv('cycle', 'mute')
    elseif id == 'preencher' then mp.set_property_number('panscan', (mp.get_property_number('panscan') or 0) > 0 and 0 or 1)
    elseif id == 'menu' then if menu == b.valor then menu = nil else menu, menuIni = b.valor, 0 end
    elseif id == 'item' then
        mp.set_property(menu == 'sub' and 'sid' or menu == 'audio' and 'aid' or 'vid', b.valor)
        menu = nil
    end
end

-- A posição do clique vem do mouse-pos na hora, e o "hover" do mpv não decide nada: com o mpv dentro da janela do app (--wid),
-- o mpv dizia hover=false com o mouse em cima, e o 1º teste (11/11) tratava todo clique em botão como clique no vídeo.
local function lerMouse()
    local m = mp.get_property_native('mouse-pos')
    if m then mouse.x, mouse.y = m.x, m.y end
    mouse.dentro = true
end

-- Esquerdo: só nos botões (clicar no vídeo para tocar/pausar fazia o grupo todo pausar por engano, e esperava 0,28 s para
-- separar do clique duplo). Direito: tocar/pausar em qualquer lugar, na hora (pedido do 2º teste).
mp.add_forced_key_binding('MBTN_LEFT', 'streemu-clique', function(e)
    if e.event == 'down' then
        lerMouse()
        local estava = mostrava
        ultimoMov = mp.get_time()
        desenhar()
        local b = estava and acharBotao() or nil
        cliques = cliques + 1
        if cliques <= 30 then manda('clique', tostring(n(mouse.x)), tostring(n(mouse.y)), b and b.id or (estava and 'video' or 'mostrar')) end -- diagnóstico no log do app
        if not estava then return end -- com os controles escondidos, o 1º clique só os mostra
        local doMenu = b and (b.id == 'item' or b.id == 'painel' or b.id == 'opcao' or b.id == 'menu')
        if menu and not b then menu = nil
        elseif menu and not doMenu then menu = nil; acao(b)
        elseif b and b.id == 'linha' then
            if est.pode and duracao() then arrastando = 'linha'; arrastar() else manda('tocar-negado') end
        elseif b and b.id == 'volume' then arrastando = 'volume'; arrastar()
        elseif b then acao(b)
        end
    elseif e.event == 'up' then
        if arrastando == 'linha' then
            manda('ir', string.format('%.3f', arrastoPos))
            est.pos, est.at = arrastoPos, mp.get_time() -- não volta para trás até o estado do app chegar
        end
        arrastando = nil
    end
    desenhar()
end, { complex = true })

mp.add_forced_key_binding('MBTN_RIGHT', 'streemu-direito', function()
    lerMouse(); ultimoMov = mp.get_time()
    manda('tocar')
    desenhar()
end)

mp.add_forced_key_binding('MBTN_LEFT_DBL', 'streemu-duplo', function()
    lerMouse(); desenhar()
    if not acharBotao() then manda('cheia') end
end)

local function roda(dv)
    lerMouse(); ultimoMov = mp.get_time()
    if menu then menuIni = menuIni - dv / 5 else mp.commandv('add', 'volume', dv) end
    desenhar()
end
mp.add_forced_key_binding('WHEEL_UP', 'streemu-roda-cima', function() roda(5) end)
mp.add_forced_key_binding('WHEEL_DOWN', 'streemu-roda-baixo', function() roda(-5) end)
-- teclado: só vale com o foco no vídeo (com o foco nos botões do app, as mesmas teclas passam pela tela do app)
-- atalho de teclado: mostra os controles por um instante para a pessoa ver o que mudou (depois de desenhar, que é local)
local function mostrarControles() ultimoMov = mp.get_time(); desenhar() end
local function volume(dv) mp.commandv('add', 'volume', dv); mostrarControles() end
mp.add_forced_key_binding('SPACE', 'streemu-espaco', function() manda('tocar'); mostrarControles() end)
mp.add_forced_key_binding('LEFT', 'streemu-esq', function() manda('pular', tostring(-PASSO)); mostrarControles() end, { repeatable = true })
mp.add_forced_key_binding('RIGHT', 'streemu-dir', function() manda('pular', tostring(PASSO)); mostrarControles() end, { repeatable = true })
mp.add_forced_key_binding('UP', 'streemu-cima', function() volume(5) end, { repeatable = true })
mp.add_forced_key_binding('DOWN', 'streemu-baixo', function() volume(-5) end, { repeatable = true })
mp.add_forced_key_binding('m', 'streemu-mudo', function() mp.commandv('cycle', 'mute'); mostrarControles() end)
mp.add_forced_key_binding('f', 'streemu-f', function() manda('cheia') end)
mp.add_forced_key_binding('l', 'streemu-fila', function() manda('fila') end) -- F já é a tela cheia
mp.add_forced_key_binding('F11', 'streemu-f11', function() manda('cheia') end)
mp.add_forced_key_binding('ESC', 'streemu-esc', function() if menu then menu = nil; desenhar() elseif est.cheia then manda('cheia') end end)

mp.observe_property('mouse-pos', 'native', function(_, m)
    if not m then return end
    if m.hover then houveHover = true end
    if m.x ~= mouse.x or m.y ~= mouse.y then ultimoMov = mp.get_time(); mouse.dentro = true end
    mouse.x, mouse.y = m.x, m.y
    -- só confia no "saiu" se o mpv já disse "entrou" alguma vez (embutido, ele pode nunca dizer)
    if houveHover and m.hover == false then mouse.dentro = false end
    if arrastando then arrastar() end
    desenhar()
end)
mp.observe_property('track-list', 'native', function(_, t) faixas = t or {}; desenhar() end)
mp.observe_property('osd-dimensions', 'native', function() desenhar() end)
mp.observe_property('display-height', 'number', function() desenhar() end)
mp.observe_property('volume', 'number', function() desenhar() end)
mp.observe_property('mute', 'bool', function() desenhar() end)
mp.add_periodic_timer(0.1, desenhar)

-- ------------------------------------------------------------------ o app

local function lerTema(t)
    if type(t) ~= 'table' then return end
    for k, v in pairs(t) do if type(v) == 'string' and v:match('^%x%x%x%x%x%x$') then COR[k] = v end end
end

mp.register_script_message('streemu-estado', function(json)
    local t = utils.parse_json(json or '')
    if type(t) ~= 'table' then return end
    if not avisouPronto then avisouPronto = true; manda('pronto') end -- o pipe do app só conecta depois que o script carregou
    if (t.estado or '') ~= est.estado then ultimoMov = mp.get_time() end -- aviso novo aparece mesmo com o filme rodando
    if (t.tocando == true) ~= est.tocando then ultimoMov = mp.get_time() end -- alguém pausou ou voltou a tocar: mostra os controles
    if arrastando ~= 'linha' then est.pos = tonumber(t.pos) or est.pos; est.at = mp.get_time() end
    lerTema(t.tema)
    est.dur = tonumber(t.dur)
    est.tocando = t.tocando == true
    est.pode = t.pode ~= false
    est.titulo = t.titulo or ''
    est.linha = t.linha or ''
    est.estado = t.estado or ''
    est.estadoTom = t.estadoTom or ''
    est.cheia = t.cheia == true
    est.pessoas = type(t.pessoas) == 'table' and t.pessoas or {}
    est.anfitriao = t.anfitriao == true
    est.aguardando = t.aguardando == true
    est.todos = t.todos == true
    est.transmitir = t.transmitir == true
    est.esperar = t.esperar == true
    est.esperarVisivel = t.esperarVisivel == true
    est.buffer = type(t.buffer) == 'table' and t.buffer or {}
    est.fila = type(t.fila) == 'table' and t.fila or nil
    desenhar()
end)

-- teste do visual sem mouse: {"fixo":true,"mouse":[x,y],"menu":"sub","tela":1440,"janela":[w,h]} deixa os controles na tela, finge o
-- mouse nesse ponto, abre um menu ("sessao" abre as opções do anfitrião), finge a altura do monitor e o tamanho da janela
mp.register_script_message('streemu-teste', function(json)
    local t = utils.parse_json(json or '')
    if type(t) ~= 'table' then return end
    if t.fixo ~= nil then fixo = t.fixo == true end
    if type(t.mouse) == 'table' then mouse.x, mouse.y, mouse.dentro = t.mouse[1], t.mouse[2], true end
    if t.menu ~= nil then menu = t.menu ~= '' and t.menu or nil end
    if t.tela ~= nil then alturaTela = tonumber(t.tela) end
    if type(t.janela) == 'table' then janelaTeste = { t.janela[1], t.janela[2] } end
    desenhar()
end)
