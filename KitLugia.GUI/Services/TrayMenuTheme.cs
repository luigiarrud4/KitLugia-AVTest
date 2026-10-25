using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace KitLugia.GUI.Services
{
    /// <summary>
    /// Tema ESCURO do menu de bandeja (WinForms puro), na paleta do Kit.
    ///
    /// POR QUE ISTO EXISTE: o ContextMenuStrip usava o ProfessionalRenderer padrao
    /// (fundo branco do Windows), que destoava de um app 100% escuro e, pior,
    /// ficava CLARO por cima de jogo em tela cheia. Alem disso o item marcado
    /// vinha com o quadrado azul do Windows e o separador era quase invisivel.
    ///
    /// Tudo e' desenhado a mao - nao ha imagem/skin, so cores e curvas - porque
    /// nao queremos adicionar dependencia de resource so para um menu.
    /// </summary>
    internal sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
    {
        public static readonly Color Bg = Color.FromArgb(0x1E, 0x1E, 0x1E);
        public static readonly Color BgHover = Color.FromArgb(0x2D, 0x3B, 0x4E);
        public static readonly Color Border = Color.FromArgb(0x3E, 0x3E, 0x3E);
        public static readonly Color Separator = Color.FromArgb(0x3A, 0x3A, 0x3A);
        public static readonly Color Text = Color.FromArgb(0xE8, 0xE8, 0xE8);
        public static readonly Color TextDim = Color.FromArgb(0x8A, 0x8A, 0x8A);
        public static readonly Color Gold = Color.FromArgb(0xFF, 0xD7, 0x00);
        public static readonly Color GroupLabel = Color.FromArgb(0x7C, 0x8B, 0xA0);
        /// <summary>Azul do "destaque" pedido para o motor Automatico.</summary>
        public static readonly Color Accent = Color.FromArgb(0x4F, 0xC3, 0xF7);
        /// <summary>Verde do "ligado" (check).</summary>
        public static readonly Color On = Color.FromArgb(0x4C, 0xAF, 0x50);
        /// <summary>Vermelho do "desligado" (X).</summary>
        public static readonly Color Off = Color.FromArgb(0xE5, 0x39, 0x35);

        private const int Radius = 8;

        public TrayMenuRenderer() : base(new Colors()) { RoundedEdges = false; }

        // Fontes em cache: alocar Font dentro do renderer a cada quadro vazava
        // GDI handles (o menu redesenha a cada abertura/hover).
        private static readonly Font FontTitle = new Font("Segoe UI", 9.75f, FontStyle.Bold);
        private static readonly Font FontSub = new Font("Segoe UI", 8.5f, FontStyle.Regular);
        private static readonly Font FontGroup = new Font("Segoe UI", 8.25f, FontStyle.Bold);
        private static readonly Font FontMotor = new Font("Segoe UI", 9.25f, FontStyle.Regular);
        private static readonly Font FontMotorBold = new Font("Segoe UI", 9.25f, FontStyle.Bold);

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var path = RoundedPath(new Rectangle(Point.Empty, e.ToolStrip.Size), Radius);
            using (var b = new SolidBrush(Bg)) g.FillPath(b, path);

            // CORTE REAL dos cantos. Sem isto, as bordas arredondadas eram
            // "falsas": desenhavamos o fundo com canto redondo, mas a JANELA do
            // menu continuava retangular e o que aparecia nos cantos era a tela
            // de baixo (o usuario viu "a parte branca"). A Region e' o que
            // recorta de fato o pixel.
            ApplyRoundedRegion(e.ToolStrip, e.ToolStrip.Size);
        }

        /// <summary>
        /// Aplica (e reaproveita) a Region arredondada. So recalcula quando o
        /// tamanho muda — Region a cada quadro = vazamento de GDI handle.
        /// </summary>
        private static void ApplyRoundedRegion(Control c, Size size)
        {
            try
            {
                string key = "TMREG" + size.Width + "x" + size.Height;
                if (c.Tag is string t && t.StartsWith("TMREG") && t == key) return;

                var old = c.Region;
                var region = new Region(RoundedPath(new Rectangle(Point.Empty, size), Radius));
                c.Region = region;
                old?.Dispose();
                c.Tag = key;
            }
            catch
            {
                // Region e' um detalhe cosmético: nunca deve derrubar o menu.
            }
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var p = new Pen(Border);
            g.DrawPath(p, RoundedPath(new Rectangle(Point.Empty, e.ToolStrip.Size), Radius));
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var g = e.Graphics;
            using var p = new Pen(Separator);
            int y = e.Item.Height / 2;
            // Margem maior que o padrao: o separador nao encosta nas bordas.
            int x1 = e.Item.Width / 8;
            int x2 = e.Item.Width - x1;
            g.DrawLine(p, x1, y, x2, y);
        }

        /// <summary>
        /// O ToolStripProfessionalRenderer desenha aqui o REALCE de item de menu
        /// (e a borda de foco). Sem sobrescrever, o padrao do Windows pintava um
        /// retangulo AZUL — era o que aparecia na linha de RAM quando o mouse
        /// passava por cima, dando a impressao de linha clicavel.
        ///
        /// Delega ao nosso OnRenderItemBackground, que ja faz o hover azul-ardosia
        /// apenas em item HABILITADO e SELECIONADO.
        /// </summary>
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            OnRenderItemBackground(e);
        }

        protected override void OnRenderItemBackground(ToolStripItemRenderEventArgs e)
        {
            var item = e.Item;

            // Item marcado: tick dourado. NAO para itens de estado (gb:on/gb:off):
            // esses ja desenham ✅/❌ no proprio texto (via GDI+).
            if (item is ToolStripMenuItem cmi && cmi.Checked)
            {
                string tg = cmi.Tag as string ?? "";
                if (!tg.StartsWith("gb:")) DrawCheck(e.Graphics, cmi);
            }

            // Hover SO para item clicável E selecionado. Rotulo decorativo
            // (titulo/subtitulo) nunca ganha realce — o usuario reclamou que o
            // mouse "acendia" a linha de RAM, que nao e' clicavel.
            bool rotulo = IsLabel(item);
            if (!item.Enabled || !item.Selected || rotulo)
            {
                if (rotulo && item.Tag is string ts && ts.StartsWith("group:"))
                {
                    using var b0 = new SolidBrush(Color.FromArgb(0x16, 0x16, 0x16));
                    using var p0 = RoundedPath(new Rectangle(Point.Empty, item.Size), 6);
                    e.Graphics.FillPath(b0, p0);
                }
                return;
            }

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(BgHover);
            using var path = RoundedPath(new Rectangle(Point.Empty, item.Size), 6);
            g.FillPath(b, path);
        }

        /// <summary>Rotulo decorativo (titulo/subtitulo/grupo) — nunca clicavel.</summary>
        private static bool IsLabel(ToolStripItem item)
        {
            string t = item.Tag as string ?? "";
            return t.StartsWith("title:") || t.StartsWith("subtitle:") || t.StartsWith("group:");
        }

        /// <summary>
        /// O "quadradinho" do item marcado. Esta existe e e' sobrescrivivel
        /// (verificado por reflexao — OnRenderCheck e OnRenderItemFocus NAO sao).
        /// </summary>
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            if (e.Item is ToolStripMenuItem m && m.Checked)
            {
                var r = e.ImageRectangle;
                if (r.Width > 0) DrawCheck(e.Graphics, m);
            }
        }

        /// <summary>Tick dourado do item marcado (desenhado no background).</summary>
        private static void DrawCheck(Graphics g, ToolStripMenuItem mi)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int x = mi.Padding.Left + 6;
            int y = mi.Height / 2;
            using var p = new Pen(Gold, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(p, new[]
            {
                new Point(x, y),
                new Point(x + 4, y + 4),
                new Point(x + 11, y - 5)
            });
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var item = e.Item;
            var tag = item.Tag as string ?? "";

            // TITULO: pintado aqui (o base nao desenha item desabilitado — medido 0 px).
            if (tag.StartsWith("title:"))
            {
                var rt = item.Bounds;
                using var b0 = new SolidBrush(Color.FromArgb(0x14, 0x14, 0x14));
                using var p0 = RoundedPath(rt, 6);
                e.Graphics.FillPath(b0, p0);
                TextRenderer.DrawText(e.Graphics, item.Text, FontTitle, rt, Gold,
                    TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                return;
            }

            if (tag.StartsWith("subtitle:"))
            {
                e.TextColor = TextDim;
                e.TextFont = FontSub;
                base.OnRenderItemText(e);
                return;
            }
            if (tag.StartsWith("group:"))
            {
                e.TextColor = GroupLabel;
                e.TextFont = FontGroup;
                base.OnRenderItemText(e);
                return;
            }

            // ESTADO (GameBoost): a COR do texto e' o status.
            // Nao se desenha nada aqui: para item HABILITADO o ToolStrip entrega
            // a Graphics transladada para a origem do item, e qualquer desenho com
            // coordenada ABSOLUTA (item.Bounds) sai da tela — medido: um retangulo
            // preenchido cobrindo o item inteiro produzia 0 pixels.
            // O icone verde/vermelho e' desenhado em OnRenderItemBackground.
            if (tag.StartsWith("gb:on") || tag.StartsWith("gb:off"))
            {
                bool on = tag.StartsWith("gb:on");
                bool auto = tag.EndsWith(":accent");
                e.TextColor = auto ? Accent : (on ? On : Off);
                e.TextFont = auto ? FontMotorBold : item.Font;
                base.OnRenderItemText(e);
                return;
            }

            if (item is ToolStripMenuItem mi && mi.Checked)
                e.TextColor = Gold;
            else
                e.TextColor = Text;

            base.OnRenderItemText(e);
        }

        /// <summary>Nome do motor a partir do texto ("GameBoost . X" -> "X").</summary>
        private static string MotorName(ToolStripItem item)
        {
            string t = item.Text ?? "";
            int i = t.IndexOf('\u00B7');
            return i >= 0 ? t.Substring(i + 1).Trim() : "";
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            // Seta de submenu em vez do triangulo claro do SO.
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var p = new Pen(Text);
            e.ArrowRectangle = new Rectangle(e.ArrowRectangle.X, e.ArrowRectangle.Y,
                                             e.ArrowRectangle.Width - 2, e.ArrowRectangle.Height);
            e.Graphics.DrawLines(p, new[]
            {
                new Point(e.ArrowRectangle.Left, e.ArrowRectangle.Top),
                new Point(e.ArrowRectangle.Right, e.ArrowRectangle.Top + e.ArrowRectangle.Height / 2),
                new Point(e.ArrowRectangle.Left, e.ArrowRectangle.Bottom)
            });
        }

        private static GraphicsPath RoundedPath(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = Math.Max(2, radius * 2);
            if (r.Width <= d || r.Height <= d)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private sealed class Colors : ProfessionalColorTable
        {
            public override Color MenuItemSelected => BgHover;
            public override Color MenuItemSelectedGradientBegin => BgHover;
            public override Color MenuItemSelectedGradientEnd => BgHover;
            public override Color MenuItemPressedGradientBegin => BgHover;
            public override Color MenuItemPressedGradientEnd => BgHover;
            public override Color MenuBorder => Border;
            public override Color ToolStripDropDownBackground => Bg;
            public override Color ImageMarginGradientBegin => Bg;
            public override Color ImageMarginGradientMiddle => Bg;
            public override Color ImageMarginGradientEnd => Bg;
            public override Color SeparatorDark => Separator;
            public override Color MenuItemBorder => Bg;
            public override Color SeparatorLight => Separator;
        }
    }
}