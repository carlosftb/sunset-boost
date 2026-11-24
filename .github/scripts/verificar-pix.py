"""Confere que a doação por Pix do README leva pra chave certa.

O QR code é uma imagem: o diff de um PR não mostra o que está dentro dela. Este script falha o CI se
o QR, o "copia e cola" ou a chave do README apontarem pra qualquer chave diferente da oficial.

Pra trocar a chave de propósito, mude CHAVE_OFICIAL aqui no mesmo PR (fica visível na revisão).
"""

import re
import sys
from pathlib import Path

import zxingcpp
from PIL import Image

CHAVE_OFICIAL = "f0db43bb-0fa4-4dd4-bceb-0eb4b3ba3bdd"

RAIZ = Path(__file__).resolve().parents[2]
README = RAIZ / "README.md"

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


def bloco_depois(rotulo: str, md: str) -> str | None:
    m = re.search(re.escape(rotulo) + r"\s*```[a-z]*\n(.*?)\n```", md, re.S)
    return m.group(1).strip() if m else None


md = README.read_text(encoding="utf-8")

copia_e_cola = bloco_depois("**Pix copia e cola:**", md)
chave_readme = bloco_depois("**Chave Pix (aleatória):**", md)

if copia_e_cola is None:
    erros.append("README: não achei o bloco 'Pix copia e cola'")
if chave_readme is None:
    erros.append("README: não achei o bloco 'Chave Pix (aleatória)'")
elif chave_readme != CHAVE_OFICIAL:
    erros.append(f"README: chave mostrada é {chave_readme!r}, a oficial é {CHAVE_OFICIAL!r}")

# Confere TODAS as imagens que o README mostra, não só um nome de arquivo: trocar o <img src> por outra imagem
# (com o QR de outra pessoa) também tem que falhar. Qualquer QR code em qualquer imagem tem que ser o Pix oficial.
REF = re.compile(r"""!\[[^\]]*\]\(\s*<?([^)\s>]+)|<img[^>]*?\bsrc\s*=\s*["']?([^"'\s>]+)|\bsrcset\s*=\s*["']?([^"'>]+)|^\s*\[[^\]]+\]:\s*<?(\S+?)>?\s*$""",
                 re.I | re.M)
BADGES = ("https://img.shields.io/",)  # selos de texto: não dá pra esconder QR neles
LEGIVEIS = {".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"}

refs: list[str] = []
for m in REF.finditer(md):
    if m.group(3):  # srcset="a.png 1x, b.png 2x"
        refs += [c.strip().split()[0] for c in m.group(3).split(",") if c.strip()]
    else:
        refs.append(next(g for g in m.groups() if g))

oficial_visto = False
for ref in dict.fromkeys(refs):
    if re.match(r"^[a-z][a-z0-9+.-]*:", ref, re.I) or ref.startswith("//"):
        if not ref.startswith(BADGES):
            erros.append(f"README: imagem externa {ref!r}: o CI não consegue conferir se ela tem um QR de outra chave")
        continue
    if not re.search(r"\.(png|jpe?g|gif|webp|bmp|svg)$", ref.split("?")[0].split("#")[0], re.I):
        continue  # link reference que não é imagem
    arq = (RAIZ / ref.split("?")[0].split("#")[0].lstrip("/")).resolve()
    if RAIZ not in arq.parents:
        erros.append(f"README: imagem fora do repositório {ref!r}")
        continue
    if not arq.is_file():
        erros.append(f"README: imagem {ref!r} não existe")
        continue
    if arq.suffix.lower() not in LEGIVEIS:
        erros.append(f"README: {ref!r} é {arq.suffix}, formato que o CI não consegue ler pra conferir QR code")
        continue
    for b in zxingcpp.read_barcodes(Image.open(arq)):
        if b.format != zxingcpp.BarcodeFormat.QRCode:
            continue
        if chave_do_brcode(b.text, ref) != CHAVE_OFICIAL:
            erros.append(f"{ref}: tem um QR code que não leva pra chave oficial {CHAVE_OFICIAL}")
        elif copia_e_cola is not None and b.text != copia_e_cola:
            erros.append(f"{ref}: o conteúdo do QR é diferente do 'copia e cola' do README")
        else:
            oficial_visto = True

if not oficial_visto:
    erros.append("README: nenhuma imagem mostra o QR code oficial do Pix")

if copia_e_cola is not None and chave_do_brcode(copia_e_cola, "README copia e cola") != CHAVE_OFICIAL:
    erros.append(f"README: o 'copia e cola' leva pra outra chave, não pra {CHAVE_OFICIAL}")

if erros:
    for e in erros:
        print(f"::error::{e}")
    sys.exit(1)

print(f"Pix ok: {len(dict.fromkeys(refs))} imagens do README conferidas; QR, copia e cola e chave apontam pra {CHAVE_OFICIAL}")
