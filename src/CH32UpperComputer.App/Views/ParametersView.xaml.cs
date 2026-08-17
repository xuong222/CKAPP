using System.Windows.Controls;

using CH32UpperComputer.App.ViewModels;

namespace CH32UpperComputer.App.Views
{
    /// <summary>
    /// 承载普通参数和独立安全配置流程的纯绑定视图。
    /// </summary>
    public partial class ParametersView : UserControl
    {
        /// <summary>
        /// 初始化参数页 XAML 组件。
        /// </summary>
        public ParametersView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 仅在自动化视觉验收显式指定寄存器时单选并滚动参数表，正常启动不改变界面。
        /// </summary>
        /// <param name="documentAddress">准备显示在可视区域中的四万区文档地址。</param>
        public void PrepareAutomatedCapture(int documentAddress)
        {
            if (DataContext is not ParametersViewModel viewModel)
            {
                return;
            }

            ParameterItemViewModel? target = viewModel.Registers.FirstOrDefault(
                item => item.Definition.DocumentAddress == documentAddress);

            if (target is null)
            {
                return;
            }

            viewModel.SelectItemCommand.Execute(target);
            RegistersGrid.UpdateLayout();
            RegistersGrid.ScrollIntoView(target);
            RegistersGrid.UpdateLayout();
        }
    }
}
