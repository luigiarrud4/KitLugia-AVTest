"""Regressao: exclusao do review nao pode dizer "sucesso" mentindo.

Reproduz o bug real: Core fail-closed (backup/lixeira/usuario podem recusar),
GUI contava selectedFullPaths.Count e ainda mostrava sucesso.
NAO mexe no registro, NAO chama SetAutoStart. So arquivos em %TEMP%.
"""
import io, os, shutil, sys, pathlib, tempfile

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

FALHAS = []


def Check(nome, ok, detalhe=""):
    print("  %s %-56s %s" % ("OK  " if ok else "FALHA", nome, detalhe))
    if not ok:
        FALHAS.append(nome)


RAIZ = pathlib.Path(tempfile.gettempdir()) / "kl_review_reg"
if RAIZ.exists():
    shutil.rmtree(RAIZ, ignore_errors=True)
RAIZ.mkdir(parents=True, exist_ok=True)


def p(*partes):
    return str(RAIZ.joinpath(*partes))


print("=" * 76)
print("1) SepararPorExistencia: o que decide a lista da GUI")
print("=" * 76)

# reproduz a logica do helper (a GUI nao roda aqui; o contrato e testado)
def separar(paths):
    removidos, preservados = [], []
    for x in paths:
        existe = os.path.exists(x)
        (preservados if existe else removidos).append(x)
    return removidos, preservados


# a) backup falhou / lixeira caiu -> arquivo FICOU
ficou = p("ficou.txt")
open(ficou, "w").write("x")
r, k = separar([ficou])
Check("arquivo preservado NAO sai da lista", k == [ficou] and r == [], "removidos=%d preservados=%d" % (len(r), len(k)))

# b) usuario recusou no dialogo -> arquivo FICOU (mesmo caso, mesma verdade)
recusado = p("recusado.txt")
open(recusado, "w").write("x")
r, k = separar([recusado])
Check("recusa do usuario = preservado (nao 'removido')", k == [recusado])

# c) de fato removido -> SAI da lista
saiu = p("saiu.txt")
open(saiu, "w").write("x")
os.remove(saiu)
r, k = separar([saiu])
Check("arquivo realmente removido sai da lista", r == [saiu] and k == [])

# d) mistura: 1 saiu, 1 ficou
a = p("a.txt"); open(a, "w").write("x"); os.remove(a)
b = p("b.txt"); open(b, "w").write("x")
r, k = separar([a, b])
Check("mistura separa corretamente", r == [a] and k == [b], "removidos=%d preservados=%d" % (len(r), len(k)))

# e) pasta inteira preservada
d = p("pasta"); os.makedirs(d, exist_ok=True); open(os.path.join(d, "f.txt"), "w").write("x")
r, k = separar([d])
Check("pasta que ficou e preservada", k == [d])

print()
print("=" * 76)
print("2) O BUG ANTIGO (regressao de codigo)")
print("=" * 76)

cs = io.open("KitLugia.GUI/Pages/AppsPage.xaml.cs", encoding="utf-8-sig", errors="replace").read()

Check("nao usa mais selectedFullPaths.Count como contador",
      "_reviewFilesDeleted += selectedFullPaths.Count" not in cs
      and "_reviewRegDeleted += selectedFullPaths.Count" not in cs)
Check("nao usa mais selectedSet para remover da lista",
      "var selectedSet = new HashSet<string>(selectedFullPaths" not in cs)
Check("filros usam o que REALMENTI sumiu (removidos)",
      cs.count("removidos.Contains(f.FullPath") == 1
      and cs.count("removidos.Contains(r.FullPath") == 1)
Check("mensagem mostra quantos ficaram + POR QUE",
      "continuam no lugar e seguem na lista" in cs and "Motivos:" in cs)
Check("reune as 3 listas do Core",
      "result.DeleteErrors" in cs and "result.FilesKept" in cs and "result.Errors" in cs)
Check("so avisa 'sucesso' quando 100% saiu E nao ha motivo",
      "removidos == pedidos && motivos.Count == 0" in cs)

print()
print("=" * 76)
print("3) Verificador de registro no Core (parser unico)")
print("=" * 76)

du = io.open("KitLugia.Core/DeepUninstaller.cs", encoding="utf-8-sig", errors="replace").read()
Check("RegistryItemStillExists existe e e publico",
      "public static bool RegistryItemStillExists(string fullPath)" in du)
Check("reusa ResolveHive (nao duplica o parser)",
      "ResolveHive(" in du.split("RegistryItemStillExists")[1][:2000])
Check("trata caminho de VALOR alem de subchave",
      "IsRegistryValuePath(fullPath)" in du)

# os dois blocos de excecao devem devolver true (preservar), nao false (remover)
_corpo = du.split("public static bool RegistryItemStillExists")[1][:2600]
Check("nao presume remocao quando nao consegue provar (os 2 catches)",
      _corpo.count("catch { return true; }") == 2,
      "catches com return true: %d" % _corpo.count("catch { return true; }"))

print()
print("=" * 76)
print("4) Encoding")
print("=" * 76)
for f in ["KitLugia.GUI/Pages/AppsPage.xaml.cs", "KitLugia.Core/DeepUninstaller.cs"]:
    raw = io.open(f, "rb").read()
    bom = raw[:3] == b"\xef\xbb\xbf"
    t = raw.decode("utf-8-sig", errors="replace")
    lf = t.count("\n") - t.count("\r\n")
    Check("%s BOM+CRLF" % f.split("/")[-1], bom and lf == 0, "BOM=%s LF-solto=%d" % (bom, lf))
    Check("%s sem U+FFFD" % f.split("/")[-1], "\ufffd" not in t)

shutil.rmtree(RAIZ, ignore_errors=True)

print()
print("=" * 76)
print("RESULTADO: %s" % ("TUDO OK" if not FALHAS else "%d FALHA(S): %s" % (len(FALHAS), FALHAS)))
print("=" * 76)
sys.exit(1 if FALHAS else 0)