# Segurança

O Sunset Boost roda com permissão de administrador, então falhas de segurança são levadas a sério.

## Como reportar

**Não abra uma issue pública** pra falhas de segurança.

Use o reporte privado do GitHub: aba **Security** › **Report a vulnerability** neste repositório. Conte o que encontrou, como reproduzir e em qual versão.

Você recebe uma resposta assim que possível. Quando a correção sair, o crédito vai pra você na página da versão, se quiser.

## Versões cobertas

Só a versão mais recente, na [página de Releases](https://github.com/carlosftb/sunset-boost/releases), recebe correções.

## O que conta como falha

Por exemplo:
- um programa **sem** admin conseguir usar o Sunset Boost pra rodar algo **com** admin;
- o app fechar ou mexer em algo da lista 🔒 Nunca fecha (Windows, antivírus, anti-cheats, jogos);
- o Modo Jogo deixar uma configuração do Windows alterada depois de **Restaurar tudo**;
- o app gravar ou apagar arquivos fora das pastas dele.

Como o app se protege está em [docs/DESENVOLVIMENTO.md](docs/DESENVOLVIMENTO.md#segurança).
