using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TxtAIEditor.Controls
{
    public sealed partial class TocSidebarView : UserControl
    {
        public TocSidebarView()
        {
            InitializeComponent();
        }

        public Grid Root => RootGrid;
        public ListView Items => TocListView;

        public event ItemClickEventHandler? ItemClick;

        public void Localize(Func<string, string, string> getString)
        {
            TocHeaderText.Text = getString("TOCHeader", "목차 (TOC)");
        }

        private void OnTocItemClick(object sender, ItemClickEventArgs e) => ItemClick?.Invoke(sender, e);
    }
}
