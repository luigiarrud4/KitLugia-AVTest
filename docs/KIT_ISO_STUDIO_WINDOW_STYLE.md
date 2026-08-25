# KIT ISO STUDIO — Window Style (Salvo)

**Arquivo:** `KitLugia.GUI\Windows\KitIsoStudioWindow.xaml`  
**Base:** `PathExplorerWindow.xaml` (Guardian) — WindowChrome Caption 0, ResizeBorder 6, CenterOwner

## Como salvar e reusar

Copie estes blocos para qualquer nova Window que precise cobrir o kit inteiro e ser nítida:

```xml
<Window TextOptions.TextFormattingMode="Display"
        TextOptions.TextRenderingMode="ClearType"
        SnapsToDevicePixels="True" UseLayoutRounding="True"
        Background="#0F0F0F" ShowInTaskbar="False"
        WindowStartupLocation="CenterOwner">
    <WindowChrome.WindowChrome>
        <WindowChrome CaptionHeight="0" GlassFrameThickness="0" CornerRadius="0" UseAeroCaptionButtons="False" ResizeBorderThickness="6"/>
    </WindowChrome.WindowChrome>
    <Border BorderBrush="#FFD700" BorderThickness="1">
        <Grid Margin="14">
            <!-- Header com DragMove -->
            <Grid MouseLeftButtonDown="Header_MouseDown">
                <!-- ... -->
                <Button Content="↔" Click="BtnToggleMaximize_Click" ToolTip="Esticar/Restaurar"/>
                <Button Content="✕" Click="BtnClose_Click"/>
            </Grid>
            <!-- TabControl com StudioTab -->
        </Grid>
    </Border>
</Window>
```

Code-behind:
```csharp
private void Header_MouseDown(object sender, MouseButtonEventArgs e) { if(e.LeftButton==Pressed) try{DragMove();}catch{} }
private void BtnToggleMaximize_Click(object s, RoutedEventArgs e) => WindowState = WindowState==Maximized ? Normal : Maximized;
```

## Legibilidade

- Fundo opaco `#0F0F0F` (não `#EE000000` semi)
- Sem `DropShadowEffect` no conteúdo
- `StudioCard #131313 Border #2A2A2A` + `Text #EEEEEE/#CCCCCC` + `FontSize 11.5`
- Botões `Background #2A2A2A Foreground White Border #444` (não `#1A1A1A` quase preto)
