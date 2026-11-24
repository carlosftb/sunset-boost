"""Testes do verificar-pix.py: o README original passa e cada forma conhecida de mostrar outro Pix falha.

Cada caso copia o README, as imagens e o FUNDING.yml pra uma pasta temporária, altera e roda o verificador.
Uso: python testar-verificar-pix.py
"""

import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

import segno
from PIL import Image

AQUI = Path(__file__).resolve().parent
RAIZ = AQUI.parents[1]
VERIFICADOR = AQUI / "verificar-pix.py"
CHAVE_OFICIAL = "f0db43bb-0fa4-4dd4-bceb-0eb4b3ba3bdd"
CHAVE_FALSA = "00000000-0000-4000-8000-000000000000"


def crc16(dados: str) -> str:
    crc = 0xFFFF
    for b in dados.encode("utf-8"):
        crc ^= b << 8
        for _ in range(8):
            crc = ((crc << 1) ^ 0x1021) & 0xFFFF if crc & 0x8000 else (crc << 1) & 0xFFFF
    return f"{crc:04X}"


def brcode(chave: str) -> str:
    conta = "0014br.gov.bcb.pix" + f"01{len(chave):02d}{chave}"
    p = f"00020126{len(conta):02d}{conta}" + "52040000" + "5303986" + "5802BR" + "5906FULANO" + "6009SAO PAULO" + "62070503***6304"
    return p + crc16(p)


README = (RAIZ / "README.md").read_text(encoding="utf-8")
OFICIAL = re.search(r"\*\*Pix copia e cola:\*\*\s*```[a-z]*\n(.*?)\n```", README, re.S).group(1).strip()
FALSO = brcode(CHAVE_FALSA)
IMG_OFICIAL = '<img src="docs/img/pix-qr.png"'


def qr(destino: Path, texto: str) -> None:
    segno.make(texto, error="m").save(str(destino), scale=8, border=4)


def qr_falso(d: Path, nome: str = "x.png") -> Path:
    arq = d / "docs" / "img" / nome
    qr(arq, FALSO)
    return arq


def depois_do_qr(md: str, extra: str) -> str:
    """Põe `extra` logo depois da imagem oficial do QR."""
    i = md.index(IMG_OFICIAL)
    j = md.index(">", i) + 1
    return md[:j] + "\n  " + extra + md[j:]


def troca_simples(d, md):
    qr_falso(d)
    return md.replace("docs/img/pix-qr.png", "docs/img/x.png")


def alt_com_maior(d, md):
    qr_falso(d)
    return depois_do_qr(md, '<img alt="a>b" src="docs/img/x.png" width="220">')


def entidade_html(d, md):
    qr_falso(d)
    return depois_do_qr(md, '<img src="docs/img/x&#46;png" width="220">')


def percent_encoding(d, md):
    qr_falso(d)
    return depois_do_qr(md, '<img src="docs/img/x%2Epng" width="220">')


def sem_extensao(d, md):
    qr_falso(d).rename(d / "docs" / "img" / "qr")
    return depois_do_qr(md, '<img src="docs/img/qr" width="220">')


def referencia_com_titulo(d, md):
    qr_falso(d)
    return depois_do_qr(md, "\n\n![qr][q]\n") + '\n[q]: docs/img/x.png "QR"\n'


def srcset(d, md):
    qr_falso(d)
    return depois_do_qr(md, '<picture><source srcset="docs/img/x.png 1x"><img src="docs/img/pix-qr.png"></picture>')


def tag_image(d, md):
    qr_falso(d)
    return depois_do_qr(md, '<image src="docs/img/x.png" width="220">')


def gif_animado(d, md):
    o, e = d / "o.png", d / "e.png"
    qr(o, OFICIAL)
    qr(e, FALSO)
    a, b = Image.open(o).convert("P"), Image.open(e).convert("P")
    a.save(d / "docs" / "img" / "pix-qr.gif", save_all=True, append_images=[b], duration=[1, 60000], loop=0)
    return md.replace("docs/img/pix-qr.png", "docs/img/pix-qr.gif")


def qr_transparente(d, md):
    """QR desenhado só no canal alfa: sem fundo, convert('RGB') vira tudo preto."""
    arq = qr_falso(d)
    im = Image.open(arq).convert("L")
    rgba = Image.new("RGBA", im.size, (0, 0, 0, 0))
    rgba.putalpha(im.point(lambda v: 255 - v))
    rgba.save(arq)
    return depois_do_qr(md, '<img src="docs/img/x.png" width="220">')


def svg(d, md):
    (d / "docs" / "img" / "x.svg").write_text('<svg xmlns="http://www.w3.org/2000/svg"/>', encoding="utf-8")
    return depois_do_qr(md, '<img src="docs/img/x.svg">')


def imagem_externa(d, md):
    return depois_do_qr(md, '<img src="https://example.com/qr.png">')


def oficial_em_comentario(d, md):
    i, j = md.index("**Pix copia e cola:**"), md.index("O valor fica")
    bloco = md[i:j]
    falso = (bloco.replace(OFICIAL, FALSO).replace(CHAVE_OFICIAL, CHAVE_FALSA)
             .replace("**Pix copia e cola:**", "**Pix copia e cola** :")
             .replace("**Chave Pix (aleatória):**", "**Chave Pix (aleatória)** :"))
    return md[:i] + "<!--\n" + bloco + "-->\n" + falso + md[j:]


def segundo_codigo(d, md):
    return md.replace("O valor fica", f"**Outra opção:**\n\n```text\n{FALSO}\n```\n\nO valor fica")


def codigo_maiusculo(d, md):
    return md.replace("O valor fica", f"`{FALSO.replace('br.gov.bcb.pix', 'BR.GOV.BCB.PIX')}`\n\nO valor fica")


def codigo_com_zero_width(d, md):
    return md.replace("O valor fica", f"`{FALSO.replace('bcb.pix', 'bcb.​pix')}`\n\nO valor fica")


def outra_chave_no_texto(d, md):
    return md.replace("O valor fica", f"Ou use a chave {CHAVE_FALSA}.\n\nO valor fica")


def outra_chave_em_link(d, md):
    return md.replace("O valor fica", f"[Pix](https://example.com/?chave={CHAVE_FALSA})\n\nO valor fica")


def funding_trocado(d, md):
    (d / ".github" / "FUNDING.yml").write_text('custom: ["https://example.com/pix"]\n', encoding="utf-8")
    return md


CASOS_QUE_FALHAM = [
    troca_simples, alt_com_maior, entidade_html, percent_encoding, sem_extensao, referencia_com_titulo, srcset,
    tag_image, gif_animado, qr_transparente, svg, imagem_externa, oficial_em_comentario, segundo_codigo,
    codigo_maiusculo, codigo_com_zero_width, outra_chave_no_texto, outra_chave_em_link, funding_trocado,
]


def rodar(nome, mudar) -> bool:
    with tempfile.TemporaryDirectory() as tmp:
        d = Path(tmp)
        shutil.copytree(RAIZ / "docs" / "img", d / "docs" / "img")
        (d / ".github").mkdir()
        shutil.copy(RAIZ / ".github" / "FUNDING.yml", d / ".github" / "FUNDING.yml")
        md = mudar(d, README) if mudar else README
        (d / "README.md").write_text(md, encoding="utf-8")
        r = subprocess.run([sys.executable, str(VERIFICADOR), str(d)], capture_output=True, text=True, encoding="utf-8",
                           env={**os.environ, "PYTHONIOENCODING": "utf-8"})
    passou = r.returncode == 0
    esperado = mudar is None
    saida = (r.stdout + r.stderr).strip().splitlines()
    print(f"{'ok  ' if passou == esperado else 'ERRO'} {nome:24} {'passa' if passou else 'falha'}: {saida[0] if saida else ''}")
    return passou == esperado


resultados = [rodar("original", None)] + [rodar(f.__name__, f) for f in CASOS_QUE_FALHAM]
if not all(resultados):
    print(f"::error::{resultados.count(False)} caso(s) do verificador do Pix com resultado errado")
    sys.exit(1)
print(f"{len(resultados)} casos ok")
