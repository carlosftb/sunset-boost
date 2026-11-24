"""Confere que a doação por Pix do README leva pra chave certa.

O QR code é uma imagem: o diff de um PR não mostra o que está dentro dela. Este script falha o CI se
qualquer coisa que o README mostra (imagem com QR, código Pix ou chave aleatória) apontar pra uma chave
diferente da oficial.

Em vez de adivinhar o que o GitHub mostra lendo o Markdown com regex, o README é convertido em HTML pelo
cmark-gfm (o conversor que o próprio GitHub usa) e lido com um parser de HTML de verdade. Assim comentários
<!-- --> ficam de fora, entidades como &#46; viram o caractere certo e toda forma de imagem é encontrada.

Pra trocar a chave de propósito, mude CHAVE_OFICIAL aqui no mesmo PR (fica visível na revisão).
Uso: python verificar-pix.py [pasta do repositório]   (sem argumento: o repositório deste script)
"""

import re
import sys
import unicodedata
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote

import cmarkgfm
import zxingcpp
from cmarkgfm.cmark import Options
from PIL import Image, ImageSequence

CHAVE_OFICIAL = "f0db43bb-0fa4-4dd4-bceb-0eb4b3ba3bdd"
FUNDING_OFICIAL = "https://github.com/carlosftb/sunset-boost#-apoie-o-projeto"
BADGES = ("https://img.shields.io/",)  # selos de texto: não dá pra esconder QR neles
# Atributos que fazem o navegador baixar e mostrar uma imagem, em qualquer tag
ATRIBUTOS_DE_IMAGEM = {"src", "srcset", "poster", "background", "data", "lowsrc", "dynsrc"}
UUID = re.compile(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", re.I)

RAIZ = Path(sys.argv[1] if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]).resolve()
erros: list[str] = []


def crc16(dados: str) -> str:
    """CRC16-CCITT (0xFFFF), o do padrão BR Code do Banco Central."""
    crc = 0xFFFF
    for b in dados.encode("utf-8"):
        crc ^= b << 8
        for _ in range(8):
            crc = ((crc << 1) ^ 0x1021) & 0xFFFF if crc & 0x8000 else (crc << 1) & 0xFFFF
    return f"{crc:04X}"


def campos(texto: str) -> dict[str, str]:
    """Lê o formato ID(2) + tamanho(2) + valor do BR Code."""
    saida, i = {}, 0
    while i < len(texto):
        if i + 4 > len(texto):
            raise ValueError(f"campo truncado na posição {i}")
        id_, tam = texto[i : i + 2], int(texto[i + 2 : i + 4])
        valor = texto[i + 4 : i + 4 + tam]
        if len(valor) != tam:
            raise ValueError(f"campo {id_} com tamanho errado")
        saida[id_] = valor
        i += 4 + tam
    return saida


def chave_do_brcode(texto: str, origem: str) -> str | None:
    if len(texto) < 8 or texto[-8:-4] != "6304" or crc16(texto[:-4]) != texto[-4:]:
        erros.append(f"{origem}: CRC inválido (o código foi alterado ou está corrompido)")
    try:
        conta = campos(campos(texto)["26"])
    except (KeyError, ValueError) as e:
        erros.append(f"{origem}: BR Code mal formado ({e})")
        return None
    if conta.get("00", "").lower() != "br.gov.bcb.pix":
        erros.append(f"{origem}: não é um Pix (GUI {conta.get('00')!r})")
    return conta.get("01")


def limpar(texto: str) -> str:
    """Tira caracteres invisíveis (zero-width etc.) que poderiam esconder um código no meio do texto."""
    return "".join(c for c in texto if unicodedata.category(c) != "Cf")


class Leitor(HTMLParser):
    """Junta o texto visível e toda referência de imagem do HTML. Comentários são ignorados pelo parser."""

    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.texto: list[str] = []
        self.imagens: list[str] = []

    def handle_starttag(self, tag, attrs):
        for nome, valor in attrs:
            if valor is None:
                continue
            nome = nome.lower()
            if nome == "srcset":  # "a.png 1x, b.png 2x"
                self.imagens += [c.split()[0] for c in valor.split(",") if c.strip()]
            elif nome in ATRIBUTOS_DE_IMAGEM or (tag == "image" and nome in ("href", "xlink:href")):
                self.imagens.append(valor.strip())
            elif nome in ("title", "alt", "href"):  # também aparecem pra quem lê (dica, texto alternativo, link)
                self.texto.append(" " + valor + " ")

    handle_startendtag = handle_starttag

    def handle_data(self, data):
        self.texto.append(data)


# ---------------------------------------------------------------- README como o GitHub mostra
md = (RAIZ / "README.md").read_text(encoding="utf-8")
html = cmarkgfm.github_flavored_markdown_to_html(md, options=Options.CMARK_OPT_UNSAFE)
leitor = Leitor()
leitor.feed(html)
leitor.close()
texto = limpar("".join(leitor.texto))

# ---------------------------------------------------------------- texto: copia e cola e chave
m = re.search(r"\*\*Pix copia e cola:\*\*\s*```[a-z]*\n(.*?)\n```", md, re.S)
copia_e_cola = m.group(1).strip() if m else None
if copia_e_cola is None:
    erros.append("README: não achei o bloco 'Pix copia e cola'")
else:
    if chave_do_brcode(copia_e_cola, "README copia e cola") != CHAVE_OFICIAL:
        erros.append(f"README: o 'copia e cola' leva pra outra chave, não pra {CHAVE_OFICIAL}")
    if copia_e_cola not in texto:
        erros.append("README: o 'copia e cola' oficial não aparece pra quem lê (está escondido, num comentário?)")
    # Qualquer outro código Pix no README, com qualquer rótulo e em qualquer lugar, tem que ser o oficial
    if "br.gov.bcb.pix" in texto.replace(copia_e_cola, "").lower():
        erros.append("README: tem outro código Pix além do 'copia e cola' oficial")

if CHAVE_OFICIAL not in texto.lower():
    erros.append("README: a chave Pix oficial não aparece pra quem lê")
for outra in sorted({u.lower() for u in UUID.findall(texto)} - {CHAVE_OFICIAL}):
    erros.append(f"README: chave aleatória {outra!r} não é a oficial")

# ---------------------------------------------------------------- imagens: todo QR em todo quadro
def quadros(arq: Path):
    """Cada quadro da imagem (GIF/PNG/WebP animados têm vários), sobre fundo branco e preto se for transparente."""
    with Image.open(arq) as im:
        for q in ImageSequence.Iterator(im):
            q = q.convert("RGBA")
            for fundo in ("white", "black"):
                base = Image.new("RGBA", q.size, fundo)
                base.alpha_composite(q)
                yield base.convert("RGB")


oficial_visto = False
refs = list(dict.fromkeys(leitor.imagens))
for ref in refs:
    if re.match(r"^[a-z][a-z0-9+.-]*:", ref, re.I) or ref.startswith("//"):
        if not ref.startswith(BADGES):
            erros.append(f"README: imagem externa {ref!r}: o CI não consegue conferir se ela tem um QR de outra chave")
        continue
    caminho = unquote(ref.split("#")[0].split("?")[0]).lstrip("/")
    arq = (RAIZ / caminho).resolve()
    if RAIZ not in arq.parents:
        erros.append(f"README: imagem fora do repositório {ref!r}")
        continue
    if not arq.is_file():
        erros.append(f"README: imagem {ref!r} não existe")
        continue
    try:
        textos = {b.text for q in quadros(arq) for b in zxingcpp.read_barcodes(q) if b.format == zxingcpp.BarcodeFormat.QRCode}
    except Exception as e:  # SVG, formato desconhecido, imagem gigante...
        erros.append(f"README: não consegui ler {ref!r} pra conferir QR code ({type(e).__name__}); use PNG, JPG, GIF ou WebP")
        continue
    for t in textos:
        if chave_do_brcode(t, ref) != CHAVE_OFICIAL:
            erros.append(f"{ref}: tem um QR code que não leva pra chave oficial {CHAVE_OFICIAL}")
        elif copia_e_cola is not None and t != copia_e_cola:
            erros.append(f"{ref}: o conteúdo do QR é diferente do 'copia e cola' do README")
        else:
            oficial_visto = True

if not oficial_visto:
    erros.append("README: nenhuma imagem mostra o QR code oficial do Pix")

# ---------------------------------------------------------------- botão Sponsor
funding = RAIZ / ".github" / "FUNDING.yml"
if funding.is_file():
    for linha in funding.read_text(encoding="utf-8").splitlines():
        linha = linha.strip()
        if not linha or linha.startswith("#"):
            continue
        if linha != f'custom: ["{FUNDING_OFICIAL}"]':
            erros.append(f"FUNDING.yml: o botão Sponsor só pode levar pra {FUNDING_OFICIAL} (linha {linha!r})")

if erros:
    for e in dict.fromkeys(erros):
        print(f"::error::{e}")
    sys.exit(1)

print(f"Pix ok: {len(refs)} imagens do README conferidas; QR, copia e cola e chave apontam pra {CHAVE_OFICIAL}")
