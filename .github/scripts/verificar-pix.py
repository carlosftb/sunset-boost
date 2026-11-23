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
QR = RAIZ / "docs" / "img" / "pix-qr.png"

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

lidos = zxingcpp.read_barcodes(Image.open(QR))
qrs = [b.text for b in lidos if b.format == zxingcpp.BarcodeFormat.QRCode]
if len(qrs) != 1:
    erros.append(f"{QR.name}: esperava exatamente 1 QR code, achei {len(qrs)}")
else:
    texto_qr = qrs[0]
    if chave_do_brcode(texto_qr, QR.name) != CHAVE_OFICIAL:
        erros.append(f"{QR.name}: o QR leva pra outra chave, não pra {CHAVE_OFICIAL}")
    if copia_e_cola is not None and texto_qr != copia_e_cola:
        erros.append(f"{QR.name}: o conteúdo do QR é diferente do 'copia e cola' do README")

if copia_e_cola is not None and chave_do_brcode(copia_e_cola, "README copia e cola") != CHAVE_OFICIAL:
    erros.append(f"README: o 'copia e cola' leva pra outra chave, não pra {CHAVE_OFICIAL}")

if erros:
    for e in erros:
        print(f"::error::{e}")
    sys.exit(1)

print(f"Pix ok: QR, copia e cola e chave do README apontam pra {CHAVE_OFICIAL}")
