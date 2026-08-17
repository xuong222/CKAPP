using CH32UpperComputer.App.Tests.TestSupport;
using CH32UpperComputer.App.ViewModels;
using CH32UpperComputer.Core.Protocol;

using CH32UpperComputer.Infrastructure.Logging;

namespace CH32UpperComputer.App.Tests.ViewModels
{
    /// <summary>
    /// 验证统一寄存器表的单项读取、范围读取和安全单项/批量修改策略。
    /// </summary>
    [TestFixture]
    public sealed class ParametersViewModelTests
    {
        /// <summary>
        /// 验证完整寄存器表包含 40001 至 40036，任意选择跨度会更新中间寄存器当前值。
        /// </summary>
        [Test]
        public async Task ReadSelected_UpdatesEveryRegisterInRequestedSpan()
        {
            await using AppViewModelHarness harness = AppViewModelHarness.Create();
            harness.Device.SetRegister(0x0002, 2534);
            harness.Device.SetRegister(0x0003, 321);
            harness.Device.SetRegister(0x0004, 45);
            await harness.ConnectAsync();
            ParameterItemViewModel first = FindItem(harness, 40003);
            ParameterItemViewModel last = FindItem(harness, 40005);
            first.IsSelected = true;
            last.IsSelected = true;

            Task read = harness.Parameters.ReadSelectedCommand.ExecuteAsync(null);
            await harness.RespondToWriteAsync(1);
            await read.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(harness.Parameters.Registers, Has.Count.EqualTo(36));
                Assert.That(first.CurrentValueText, Is.EqualTo("25.34 ℃"));
                Assert.That(
                    FindItem(harness, 40004).CurrentValueText,
                    Is.EqualTo("321 MQ2 线性单位"));
                Assert.That(last.CurrentValueText, Is.EqualTo("45 μg/m³"));
            }));
        }

        /// <summary>
        /// 验证单项修改使用 0x06，连续多项修改使用一个 0x10。
        /// </summary>
        [Test]
        public async Task ModifySingleThenContiguousSelection_Uses06Then10()
        {
            await using AppViewModelHarness harness = AppViewModelHarness.Create();
            await harness.ConnectAsync();
            ParameterItemViewModel threshold = FindItem(harness, 40011);
            threshold.InputText = "60";

            Task singleWrite = harness.Parameters.ModifyItemCommand.ExecuteAsync(threshold);
            await harness.RespondToWriteAsync(1);
            await singleWrite.WaitAsync(TimeSpan.FromSeconds(1));
            SetOnlySelected(harness, 40011, 40012);
            threshold.InputText = "70";
            FindItem(harness, 40012).InputText = "-5";

            Task multipleWrite = harness.Parameters.ModifySelectedCommand.ExecuteAsync(null);
            await harness.RespondToWriteAsync(2);
            await multipleWrite.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(
                    harness.Transport.WrittenFrames[0].Span[1],
                    Is.EqualTo((byte)ModbusFunctionCode.WriteSingleRegister));
                Assert.That(
                    harness.Transport.WrittenFrames[1].Span[1],
                    Is.EqualTo((byte)ModbusFunctionCode.WriteMultipleRegisters));
                Assert.That(harness.Transport.WrittenFrames, Has.Count.EqualTo(2));
            }));
        }

        /// <summary>
        /// 验证非连续选择先回读跨度，再把中间寄存器最新原始值合并进唯一 0x10。
        /// </summary>
        [Test]
        public async Task ModifyNonContiguousSelection_ReadsAndPreservesGapBefore10()
        {
            await using AppViewModelHarness harness = AppViewModelHarness.Create();
            harness.Device.SetRegister(0x000B, 0xFFFB);
            await harness.ConnectAsync();
            SetOnlySelected(harness, 40011, 40013);
            FindItem(harness, 40011).InputText = "65";
            FindItem(harness, 40013).InputText = "250";

            Task write = harness.Parameters.ModifySelectedCommand.ExecuteAsync(null);
            await harness.RespondToWriteAsync(1);
            await harness.RespondToWriteAsync(2);
            await write.WaitAsync(TimeSpan.FromSeconds(1));
            byte[] frame = harness.Transport.WrittenFrames[1].ToArray();

            Assert.Multiple((Action)(() =>
            {
                Assert.That(
                    harness.Transport.WrittenFrames[0].Span[1],
                    Is.EqualTo((byte)ModbusFunctionCode.ReadHoldingRegisters));
                Assert.That(frame[1], Is.EqualTo((byte)ModbusFunctionCode.WriteMultipleRegisters));
                Assert.That(frame[5], Is.EqualTo(3));
                Assert.That(frame[9], Is.EqualTo(0xFF));
                Assert.That(frame[10], Is.EqualTo(0xFB));
                Assert.That(harness.Device.GetRegister(0x000B), Is.EqualTo(0xFFFB));
            }));
        }

        /// <summary>
        /// 验证无效输入或跨越只读区的组间选择都保持零写入。
        /// </summary>
        [Test]
        public async Task ModifySelected_InvalidOrCrossGroup_PerformsZeroWrites()
        {
            await using AppViewModelHarness harness = AppViewModelHarness.Create();
            await harness.ConnectAsync();
            ParameterItemViewModel invalid = FindItem(harness, 40011);
            invalid.IsSelected = true;
            invalid.InputText = "不是数字";

            await harness.Parameters.ModifySelectedCommand.ExecuteAsync(null);
            Assert.That(harness.Transport.WrittenFrames, Is.Empty);

            invalid.InputText = "50";
            FindItem(harness, 40030).IsSelected = true;
            await harness.Parameters.ModifySelectedCommand.ExecuteAsync(null);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(harness.Transport.WrittenFrames, Is.Empty);
                Assert.That(harness.Parameters.StatusMessage, Does.Contain("不能跨越"));
            }));
        }

        /// <summary>
        /// 验证普通读取和地址专用修改产生的完整原始帧都会进入实时监控共用的数据收发列表。
        /// </summary>
        [Test]
        public async Task ParameterStandardAndSpecialTransactions_AppearInSharedConsole()
        {
            await using AppViewModelHarness harness = AppViewModelHarness.Create();
            await harness.ConnectAsync();
            ParameterItemViewModel temperature = FindItem(harness, 40003);

            Task read = harness.Parameters.ReadItemCommand.ExecuteAsync(temperature);
            await harness.RespondToWriteAsync(1);
            await read.WaitAsync(TimeSpan.FromSeconds(1));

            harness.Parameters.TargetSlaveAddress = 2;
            Task changeAddress =
                harness.Parameters.ChangeAddressCommand.ExecuteAsync(null);
            await harness.RespondToWriteAsync(2);
            await changeAddress.WaitAsync(TimeSpan.FromSeconds(1));
            harness.CommunicationLog.FlushPendingEntries();

            CommunicationLogEntry[] lineEntries = harness.CommunicationLog.Entries
                .Where(entry =>
                    entry.Direction is CommunicationDirection.Transmit or
                        CommunicationDirection.Receive)
                .ToArray();

            Assert.Multiple((Action)(() =>
            {
                Assert.That(lineEntries, Has.Length.EqualTo(4));
                Assert.That(
                    lineEntries.Count(
                        entry => entry.Direction == CommunicationDirection.Transmit),
                    Is.EqualTo(2));
                Assert.That(
                    lineEntries.Count(
                        entry => entry.Direction == CommunicationDirection.Receive),
                    Is.EqualTo(2));
                Assert.That(
                    lineEntries.Select(entry => entry.TransactionId)
                        .Distinct()
                        .Count(),
                    Is.EqualTo(2));
                Assert.That(
                    lineEntries.Any(
                        entry =>
                            entry.RawHex ==
                            HexFrameParser.Format(
                                harness.Transport.WrittenFrames[1].Span)),
                    Is.True);
            }));
        }

        /// <summary>
        /// 验证批量读取允许任意寄存器，而批量修改只允许同一普通可写组。
        /// </summary>
        [Test]
        public async Task SelectedCommands_SeparateReadAndWriteEligibility()
        {
            await using AppViewModelHarness harness = AppViewModelHarness.Create();
            await harness.ConnectAsync();
            ParameterItemViewModel readOnly = FindItem(harness, 40009);
            ParameterItemViewModel alarmWritable = FindItem(harness, 40017);
            ParameterItemViewModel compensationWritable = FindItem(harness, 40030);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(
                    harness.Parameters.ReadSelectedCommand.CanExecute(null),
                    Is.False);
                Assert.That(
                    harness.Parameters.ModifySelectedCommand.CanExecute(null),
                    Is.False);
            }));

            readOnly.IsSelected = true;

            Assert.Multiple((Action)(() =>
            {
                Assert.That(
                    harness.Parameters.ReadSelectedCommand.CanExecute(null),
                    Is.True);
                Assert.That(
                    harness.Parameters.ModifySelectedCommand.CanExecute(null),
                    Is.False);
            }));

            readOnly.IsSelected = false;
            alarmWritable.IsSelected = true;

            Assert.Multiple((Action)(() =>
            {
                Assert.That(
                    harness.Parameters.ReadSelectedCommand.CanExecute(null),
                    Is.True);
                Assert.That(
                    harness.Parameters.ModifySelectedCommand.CanExecute(null),
                    Is.True);
            }));

            compensationWritable.IsSelected = true;

            Assert.Multiple((Action)(() =>
            {
                Assert.That(
                    harness.Parameters.ReadSelectedCommand.CanExecute(null),
                    Is.True);
                Assert.That(
                    harness.Parameters.ModifySelectedCommand.CanExecute(null),
                    Is.False);
            }));
        }

        /// <summary>
        /// 验证点击寄存器名称只保留当前行，避免旧选择隐藏在滚动区域外。
        /// </summary>
        [Test]
        public async Task SelectItemCommand_UsesExclusiveSingleSelection()
        {
            await using AppViewModelHarness harness = AppViewModelHarness.Create();
            ParameterItemViewModel oldSelection = FindItem(harness, 40009);
            ParameterItemViewModel target = FindItem(harness, 40017);
            oldSelection.IsSelected = true;

            harness.Parameters.SelectItemCommand.Execute(target);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(oldSelection.IsSelected, Is.False);
                Assert.That(target.IsSelected, Is.True);
                Assert.That(
                    harness.Parameters.Registers.Count(item => item.IsSelected),
                    Is.EqualTo(1));
            }));
        }

        /// <summary>
        /// 根据文档地址取得测试参数行。
        /// </summary>
        /// <param name="harness">包含完整参数 ViewModel 的应用测试环境。</param>
        /// <param name="documentAddress">需要取得的四万区文档地址。</param>
        /// <returns>与文档地址唯一对应的统一寄存器表行。</returns>
        private static ParameterItemViewModel FindItem(
            AppViewModelHarness harness,
            int documentAddress)
        {
            return harness.Parameters.Registers.Single(
                item => item.Definition.DocumentAddress == documentAddress);
        }

        /// <summary>
        /// 清空全部选择后只选中指定文档地址。
        /// </summary>
        /// <param name="harness">包含完整参数 ViewModel 的应用测试环境。</param>
        /// <param name="documentAddresses">需要保持选中的文档地址。</param>
        private static void SetOnlySelected(
            AppViewModelHarness harness,
            params int[] documentAddresses)
        {
            HashSet<int> selected = new(documentAddresses);

            foreach (ParameterItemViewModel item in harness.Parameters.Registers)
            {
                item.IsSelected = selected.Contains(item.Definition.DocumentAddress);
            }
        }
    }
}
