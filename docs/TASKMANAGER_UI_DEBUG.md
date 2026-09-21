# Debug visual do TaskManager via harness WPF (janela REAL, sem abrir o Kit)

> Como interagir/programar contra a UI do `KitTaskManagerWindow` para validar mudanças
> de layout, expand/collapse, abas e cores — com **screenshots** e **acesso ao estado
> interno** — sem clicar à mão nem adivinhar pelo código.

## Por que funciona

O `KitTaskManagerWindow` é uma `Window` WPF comum. WPF exige thread STA + bomba de
mensagens (dispatcher). Um **projeto de console com `<UseWPF>true</UseWPF>`** que
referencia `KitLugia.GUI.csproj` pode criar a janela numa thread STA própria, mostrá-la
e continuar controlando tudo **por código** a partir da thread principal via
`Dispatcher.InvokeAsync`. É literalmente "abrir o gerenciador e usar o mouse/teclado
por programação", com direito a foto da tela e leitura de campos privados.

## A receita (3 peças)

### 1. Projeto de harness (`.tmp-tm-harness/tmharness.csproj`)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>disable</Nullable>
    <AssemblyName>tmharness</AssemblyName>
    <StartupObject>tmharness.Program</StartupObject>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\KitLugia.GUI\KitLugia.GUI.csproj" />
  </ItemGroup>
</Project>
```

### 2. Janela real em thread STA + ponte `Invoke`

```csharp
private static Window _win;
private static Thread _sta;

_sta = new Thread(() =>
{
    _win = new KitTaskManagerWindow();     // a MESMA janela que o usuário abre
    _win.Left = 40; _win.Top = 40; _win.Width = 1280; _win.Height = 800;
    _win.Show();                            // NÃO usar ShowDialog
    System.Windows.Threading.Dispatcher.Run();  // bomba de mensagens da thread
});
_sta.SetApartmentState(ApartmentState.STA);
_sta.Start();

// Toda interação passa por aqui (a thread principal NÃO é a thread da UI):
private static T Invoke<T>(Func<T> f)
{
    T res = default;
    var done = new ManualResetEvent(false);
    _win.Dispatcher.InvokeAsync(() => { try { res = f(); } finally { done.Set(); } });
    done.WaitOne(8000);
    return res;
}
```

**Armadilhas que já morderam:**
- `IsLoaded`/qualquer propriedade de `DependencyObject` **só na thread da UI** — leia
  via `Invoke`, senão `InvalidOperationException` de cross-thread.
- O `_win` nasce na thread STA; se o `Invoke` rodar antes do `_win` existir, o
  dispatcher ainda é nulo. Fallback: `Dispatcher.FromThread(_sta)` + polling até a
  janela carregar.
- `DispatcherTimer` de prioridade baixa entra na fila ATRÁS dos callbacks `Rendering`
  do motor de 60fps — para medir **stall real** da UI, meça de uma worker thread com
  `InvokeAsync` (latência até o dispatcher responder), não com um timer na própria UI.

### 3. Métodos-chave (todos validados nesta sessão)

```csharp
// ── Abrir a aba certa ──────────────────────────────────────────────
// A grid de processos SÓ publica dados com a aba Processos ativa
// (o ApplyFilter/refresh gated por aba). Chamar o SwitchTab direto:
var btn = TM.FindName("BtnTabProcesses") as Button;
TM.GetType().GetMethod("SwitchTab", BindingFlags.NonPublic | BindingFlags.Instance)
  ?.Invoke(TM, new object[] { btn, null });
// Se precisar forçar: ApplyFilter("") também é invocável por reflexão (assinatura: (string)).

// ── Ler estado interno (linhas vivas da grid) ─────────────────────
// _groupedLive é ObservableCollection<ProcessRow> => cast em IList (NÃO List!):
var live = TM.GetType()
    .GetField("_groupedLive", BindingFlags.NonPublic | BindingFlags.Instance)
    ?.GetValue(TM) as System.Collections.Generic.IList<KitTaskManagerWindow.ProcessRow>;

// ProcessRow é classe aninhada: referencie como KitTaskManagerWindow.ProcessRow.

// ── Chamar ToggleExpand (expand/collapse real) ────────────────────
TM.GetType().GetMethod("ToggleExpand", BindingFlags.NonPublic | BindingFlags.Instance)
  ?.Invoke(TM, new object[] { row });

// ── Contador confiável de linhas renderizadas ─────────────────────
var dg = TM.FindName("DgProcesses") as DataGrid;
int items = dg.Items.Count;   // isto reflete o CollectionView, nunca mente
```

### 4. Screenshot da janela REAL (RenderTargetBitmap)

```csharp
var png = Invoke(() =>
{
    int w = (int)Math.Ceiling(_win.ActualWidth), h = (int)Math.Ceiling(_win.ActualHeight);
    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
    rtb.Render(_win);                    // captura o conteúdo WPF renderizado
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(rtb));
    using var ms = new MemoryStream();
    enc.Save(ms);
    return ms.ToArray();
});
File.WriteAllBytes($"shot-{nome}.png", png);
```

Isso rendeu as 6 telas usadas para validar o re-tema (Resumo, Processos normal e
expandido, Performance, Latência, Usuários) **sem abrir o Kit de verdade**.

## O fluxo de trabalho que usamos (e que funcionou)

1. **Diagnóstico estático primeiro**: `code_search`/`grep` no XAML + code-behind para
   achar o mecanismo (triggers, ToggleExpand, merge de refresh). Metade dos bugs
   (ordem de triggers, margens) se acha lendo.
2. **Hipótese → código → harness**: editar o XAML/C#, montar o harness num diretório
   temporário, `dotnet build` + `dotnet run`.
3. **Verificação comportamental**: asserts sobre `_groupedLive` (N filhos após o pai,
   zero duplicados, zero órfãos, collapse limpa tudo, re-expand restaura, refresh de 1s
   preserva) — é o que pegou o bug do loop de `RemoveAt` e o merge de instâncias.
4. **Verificação visual**: `RenderTargetBitmap` de cada aba — é o que valida cores,
   indicador da aba ativa, bloco contínuo de filhos, alinhamento do `└─`. Coisas que
   nenhum assert pega.
5. **Limpeza**: `rm -rf .tmp-tm-harness` no fim (nunca commitar o harness).

## Regras de ouro

- **A janela do harness É a janela real** — mesmos bindings, mesmos timers, mesmo motor
  de render. O que passa aqui se comporta igual no Kit.
- `DgProcesses.Items.Count` > reflexão cega: se a reflexão diz 0 mas a grid tem itens,
  desconfie do cast (`ObservableCollection` ≠ `List`) e do gating por aba.
- Screenshot **depois** de dormir o suficiente para o 1º refresh (~2,5-3,5s), senão
  fotografa a tela vazia e a conclusão é errada.
- Para medir custo do motor de render: probe DENTRO do `OnRendering` (tempo por
  callback), não `Stopwatch` entre frames — a variância externa da máquina (AV,
  llama-server) engole o sinal.
- Encoding: o projeto usa UTF-8+BOM + CRLF; o harness vive fora do controle de versão,
  então não precisa seguir, mas os arquivos editados do Kit sim (verificar antes do
  build final).
