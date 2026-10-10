using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TxtAIEditor.Controls
{
    public sealed partial class TocSidebarView : UserControl
    {
        private string _headerTitle = string.Empty;
        private string? _headerCountFormat;

        public TocSidebarView()
        {
            InitializeComponent();
            TocListView.Items.VectorChanged += (_, _) => UpdateHeader();
        }

        public Grid Root => RootGrid;
        public ListView Items => TocListView;

        public event ItemClickEventHandler? ItemClick;

        public void Localize(Func<string, string, string> getString)
        {
            _headerTitle = getString("TOCHeader", "목차 (TOC)");
            _headerCountFormat = getString("TOCHeaderCountFormat", "{0} - {1}항목");
            UpdateHeader();
        }

        private void UpdateHeader()
        {
            if (_headerCountFormat == null) return;
            TocHeaderText.Text = string.Format(
                CultureInfo.CurrentCulture, _headerCountFormat, _headerTitle, TocListView.Items.Count);
        }

        private void OnTocItemClick(object sender, ItemClickEventArgs e) => ItemClick?.Invoke(sender, e);
    }
}
