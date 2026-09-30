#!/usr/bin/env python3
"""pages_accent_audit.py — palavras em portugues sem acento nos TEXTOS VISIVEIS.

Le os atributos Text/Content/ToolTip/Header/ToolTipText dos XAML e procura
palavras que ficam erradas sem acento (nao, usuario, configuracao, ...).

Uso:  python3 tests/pages_accent_audit.py [pasta]
"""
import re
import sys
from pathlib import Path

def out(s=''):
    print(str(s).encode('ascii', 'replace').decode('ascii'))

# palavra sem acento -> forma correta (todas com frente de palavra em PT)
WORDS = {
    'nao': 'não', 'sao': 'são', 'usuario': 'usuário', 'usuarios': 'usuários',
    'configuracao': 'configuração', 'configuracoes': 'configurações',
    'voce': 'você', 'sera': 'será', 'tambem': 'também', 'apos': 'após',
    'padrao': 'padrão', 'padroes': 'padrões', 'opcao': 'opção', 'opcoes': 'opções',
    'acao': 'ação', 'acoes': 'ações', 'versao': 'versão', 'versoes': 'versões',
    'conexao': 'conexão', 'protecao': 'proteção', 'aplicacao': 'aplicação',
    'aplicacoes': 'aplicações', 'selecao': 'seleção', 'descricao': 'descrição',
    'atencao': 'atenção', 'excecao': 'exceção', 'execucao': 'execução',
    'instalacao': 'instalação', 'informacao': 'informação', 'informacoes': 'informações',
    'restauracao': 'restauração', 'verificacao': 'verificação', 'definicoes': 'definições',
    'pasta': None, 'arquivo': None,  # sem acento mesmo
    'criacao': 'criação', 'conteudo': 'conteúdo', 'recomendado': None,
    'calibragem': 'calibração', 'otimizacao': 'otimização', 'otimizacoes': 'otimizações',
    'analise': 'análise', 'memoria': 'memória', 'historico': 'histórico',
    'automatico': 'automático', 'automatica': 'automática', 'unico': 'único',
    'possivel': 'possível', 'disponivel': 'disponível', 'nivel': 'nível',
}
WORDS = {k: v for k, v in WORDS.items() if v}

ATTR = re.compile(r'(Text|Content|ToolTip|Header|ToolTipText)="([^"]{2,})"')
WORD = re.compile(r'\b(' + '|'.join(WORDS) + r')\b', re.I)

pages = Path(sys.argv[1]) if len(sys.argv) > 1 else Path('KitLugia.GUI/Pages')
n = 0
for f in sorted(pages.rglob('*.xaml')):
    src = f.read_text(encoding='utf-8-sig')
    for i, line in enumerate(src.splitlines(), 1):
        for m in ATTR.finditer(line):
            # ignora entidades (ja escapadas) e bindings
            if '{Binding' in m.group(2) or '&#x' in m.group(2):
                continue
            hit = WORD.search(m.group(2))
            if hit:
                n += 1
                out(f'  {f.as_posix()}:{i}  "{m.group(2)[:60]}"  -> sugerido: {WORDS[hit.group(1).lower()]}')
out(f'\n  ({n} ocorrencia(s))')
