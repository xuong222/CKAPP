using CH32UpperComputer.Core.Registers;

namespace CH32UpperComputer.Core.Tests.Registers
{
    /// <summary>
    /// 验证设备寄存器表逐项符合独立固化的 40001 至 40036 设备协议规格。
    /// </summary>
    [TestFixture]
    public sealed class DeviceRegisterMapTests
    {
        /// <summary>
        /// 测试内独立固化的 36 项寄存器规格；不得从生产寄存器表反向生成。
        /// </summary>
        private static readonly ExpectedRegisterSpecification[] FixedSpecifications =
        [
            new(40001, 0x0000, "从站地址", RegisterRawEncoding.UInt16Address, 1, 64, 1, 64, 1m, 0, "地址", "闭区间 1 至 64"),
            new(40002, 0x0001, "波特率代码", RegisterRawEncoding.UInt16Enumeration, 0, 5, 0, 5, 1m, 0, "代码", "0=2400, 1=4800, 2=9600, 3=19200, 4=38400, 5=57600"),
            new(40003, 0x0002, "温度显示值", RegisterRawEncoding.Int16Measurement, -5000, 11000, null, null, 0.01m, 2, "℃", "原始值除以 100"),
            new(40004, 0x0003, "烟雾显示值", RegisterRawEncoding.Int16Measurement, -1000, 5095, null, null, 1m, 0, "MQ2 线性单位", "非标定 ppm"),
            new(40005, 0x0004, "PM2.5 显示值", RegisterRawEncoding.Int16Measurement, -200, 1199, null, null, 1m, 0, "μg/m³", "整数显示"),
            new(40006, 0x0005, "CO 显示值", RegisterRawEncoding.Int16Measurement, -200, 10200, null, null, 0.1m, 1, "ppm", "原始值除以 10"),
            new(40007, 0x0006, "CO2 显示值", RegisterRawEncoding.Int16Measurement, -600, 6000, null, null, 1m, 0, "ppm", "整数显示"),
            new(40008, 0x0007, "SO2 显示值", RegisterRawEncoding.Int16Measurement, -20, 220, null, null, 0.1m, 1, "ppm", "原始值除以 10"),
            new(40009, 0x0008, "综合报警继电器", RegisterRawEncoding.UInt16Boolean, 0, 1, null, null, 1m, 0, "状态", "0=正常, 1=报警"),
            new(40010, 0x0009, "报警位", RegisterRawEncoding.UInt16BitMask, 0, 0x003F, null, null, 1m, 0, "掩码", "bit0 温度, bit1 烟雾, bit2 PM2.5, bit3 CO, bit4 CO2, bit5 SO2"),
            new(40011, 0x000A, "温度报警阈值", RegisterRawEncoding.Int16Threshold, -40, 100, -40, 100, 1m, 0, "℃", "整数阈值"),
            new(40012, 0x000B, "温度报警回差", RegisterRawEncoding.Int16Hysteresis, -32768, 0, -32768, 0, 1m, 0, "℃", "必须小于或等于零"),
            new(40013, 0x000C, "烟雾报警阈值", RegisterRawEncoding.Int16Threshold, 0, 5000, 0, 5000, 1m, 0, "MQ2 线性单位", "整数阈值"),
            new(40014, 0x000D, "烟雾报警回差", RegisterRawEncoding.Int16Hysteresis, -32768, 0, -32768, 0, 1m, 0, "MQ2 线性单位", "必须小于或等于零"),
            new(40015, 0x000E, "PM2.5 报警阈值", RegisterRawEncoding.Int16Threshold, 0, 999, 0, 999, 1m, 0, "μg/m³", "整数阈值"),
            new(40016, 0x000F, "PM2.5 报警回差", RegisterRawEncoding.Int16Hysteresis, -32768, 0, -32768, 0, 1m, 0, "μg/m³", "必须小于或等于零"),
            new(40017, 0x0010, "CO 报警阈值", RegisterRawEncoding.Int16Threshold, 0, 1000, 0, 1000, 1m, 0, "ppm", "整数阈值"),
            new(40018, 0x0011, "CO 报警回差", RegisterRawEncoding.Int16Hysteresis, -32768, 0, -32768, 0, 1m, 0, "ppm", "必须小于或等于零"),
            new(40019, 0x0012, "CO2 报警阈值", RegisterRawEncoding.Int16Threshold, 400, 5000, 400, 5000, 1m, 0, "ppm", "整数阈值"),
            new(40020, 0x0013, "CO2 报警回差", RegisterRawEncoding.Int16Hysteresis, -32768, 0, -32768, 0, 1m, 0, "ppm", "必须小于或等于零"),
            new(40021, 0x0014, "SO2 报警阈值", RegisterRawEncoding.Int16Threshold, 0, 20, 0, 20, 1m, 0, "ppm", "整数阈值"),
            new(40022, 0x0015, "SO2 报警回差", RegisterRawEncoding.Int16Hysteresis, -32768, 0, -32768, 0, 1m, 0, "ppm", "必须小于或等于零"),
            new(40023, 0x0016, "报警使能掩码", RegisterRawEncoding.UInt16BitMask, 0, 0x003F, 0, 0x003F, 1m, 0, "掩码", "仅低六位可写，可使用十六进制或逐项开关"),
            new(40024, 0x0017, "实测温度", RegisterRawEncoding.Int16Measurement, -4000, 10000, null, null, 0.01m, 2, "℃", "原始值除以 100"),
            new(40025, 0x0018, "实测烟雾", RegisterRawEncoding.Int16Measurement, 0, 4095, null, null, 1m, 0, "MQ2 线性单位", "非标定 ppm"),
            new(40026, 0x0019, "实测 PM2.5", RegisterRawEncoding.Int16Measurement, 0, 999, null, null, 1m, 0, "μg/m³", "整数显示"),
            new(40027, 0x001A, "实测 CO", RegisterRawEncoding.Int16Measurement, 0, 10000, null, null, 0.1m, 1, "ppm", "原始值除以 10"),
            new(40028, 0x001B, "实测 CO2", RegisterRawEncoding.Int16Measurement, 400, 5000, null, null, 1m, 0, "ppm", "整数显示"),
            new(40029, 0x001C, "实测 SO2", RegisterRawEncoding.Int16Measurement, 0, 200, null, null, 0.1m, 1, "ppm", "原始值除以 10"),
            new(40030, 0x001D, "温度补偿", RegisterRawEncoding.Int16Compensation, -10, 10, -10, 10, 1m, 0, "℃", "设备内部应用时乘以 100"),
            new(40031, 0x001E, "烟雾补偿", RegisterRawEncoding.Int16Compensation, -1000, 1000, -1000, 1000, 1m, 0, "MQ2 线性单位", "整数补偿"),
            new(40032, 0x001F, "PM2.5 补偿", RegisterRawEncoding.Int16Compensation, -200, 200, -200, 200, 1m, 0, "μg/m³", "整数补偿"),
            new(40033, 0x0020, "CO 补偿", RegisterRawEncoding.Int16Compensation, -200, 200, -200, 200, 0.1m, 1, "ppm", "原始值除以 10"),
            new(40034, 0x0021, "CO2 补偿", RegisterRawEncoding.Int16Compensation, -1000, 1000, -1000, 1000, 1m, 0, "ppm", "整数补偿"),
            new(40035, 0x0022, "SO2 补偿", RegisterRawEncoding.Int16Compensation, -20, 20, -20, 20, 0.1m, 1, "ppm", "原始值除以 10"),
            new(40036, 0x0023, "恢复出厂", RegisterRawEncoding.UInt16Command, 0, 0, 1, 1, 1m, 0, "命令", "读取恒为 0，写入 1 触发且不得保持"),
        ];

        /// <summary>
        /// 验证生产寄存器表逐项等于测试内独立固化的全部 36 项规格，而非仅满足通用范围约束。
        /// </summary>
        [Test]
        public void All_MatchesEveryFixedRegisterSpecification()
        {
            IReadOnlyList<RegisterDefinition> actualDefinitions = DeviceRegisterMap.All;

            Assert.Multiple((Action)(() =>
            {
                Assert.That(FixedSpecifications, Has.Length.EqualTo(36));
                Assert.That(actualDefinitions, Has.Count.EqualTo(36));
                Assert.That(
                    actualDefinitions.Select(definition => definition.DocumentAddress),
                    Is.EquivalentTo(FixedSpecifications.Select(specification => specification.DocumentAddress)));
                Assert.That(
                    actualDefinitions.Select(definition => definition.ProtocolAddress),
                    Is.EquivalentTo(FixedSpecifications.Select(specification => specification.ProtocolAddress)));
            }));

            foreach (ExpectedRegisterSpecification expected in FixedSpecifications)
            {
                RegisterDefinition actual = DeviceRegisterMap.GetByDocumentAddress(expected.DocumentAddress);

                Assert.Multiple((Action)(() =>
                {
                    Assert.That(actual.DocumentAddress, Is.EqualTo(expected.DocumentAddress), expected.Name);
                    Assert.That(actual.ProtocolAddress, Is.EqualTo(expected.ProtocolAddress), expected.Name);
                    Assert.That(actual.Name, Is.EqualTo(expected.Name), expected.DocumentAddress.ToString());
                    Assert.That(actual.RawEncoding, Is.EqualTo(expected.RawEncoding), expected.Name);
                    Assert.That(actual.ExpectedRawMinimum, Is.EqualTo(expected.ExpectedRawMinimum), expected.Name);
                    Assert.That(actual.ExpectedRawMaximum, Is.EqualTo(expected.ExpectedRawMaximum), expected.Name);
                    Assert.That(actual.WritableRawMinimum, Is.EqualTo(expected.WritableRawMinimum), expected.Name);
                    Assert.That(actual.WritableRawMaximum, Is.EqualTo(expected.WritableRawMaximum), expected.Name);
                    Assert.That(actual.IsWritable, Is.EqualTo(expected.WritableRawMinimum.HasValue), expected.Name);
                    Assert.That(actual.DisplayScale, Is.EqualTo(expected.DisplayScale), expected.Name);
                    Assert.That(actual.DecimalPlaces, Is.EqualTo(expected.DecimalPlaces), expected.Name);
                    Assert.That(actual.Unit, Is.EqualTo(expected.Unit), expected.Name);
                    Assert.That(actual.ValueDescription, Is.EqualTo(expected.ValueDescription), expected.Name);
                    Assert.That(
                        DeviceRegisterMap.GetByProtocolAddress(expected.ProtocolAddress),
                        Is.SameAs(actual),
                        expected.Name);
                }));
            }
        }

        /// <summary>
        /// 验证固定规格及生产表均连续、唯一覆盖 40001 至 40036 和 0x0000 至 0x0023。
        /// </summary>
        [Test]
        public void All_ContainsExactlyTheThirtySixContiguousUniqueDefinitions()
        {
            Assert.Multiple((Action)(() =>
            {
                Assert.That(
                    FixedSpecifications.Select(specification => specification.DocumentAddress),
                    Is.EqualTo(Enumerable.Range(40001, 36)));
                Assert.That(
                    FixedSpecifications.Select(specification => specification.ProtocolAddress),
                    Is.EqualTo(Enumerable.Range(0, 36).Select(address => (ushort)address)));
                Assert.That(
                    DeviceRegisterMap.All.Select(definition => definition.DocumentAddress).Distinct().ToArray(),
                    Has.Length.EqualTo(36));
                Assert.That(
                    DeviceRegisterMap.All.Select(definition => definition.ProtocolAddress).Distinct().ToArray(),
                    Has.Length.EqualTo(36));
            }));
        }

        /// <summary>
        /// 验证公开寄存器集合不能被调用方当作可变列表或数组修改。
        /// </summary>
        [Test]
        public void All_IsReadOnlyFromThePublicApi()
        {
            Assert.That(DeviceRegisterMap.All, Is.Not.InstanceOf<List<RegisterDefinition>>());
            Assert.That(DeviceRegisterMap.All, Is.Not.InstanceOf<RegisterDefinition[]>());
        }

        /// <summary>
        /// 验证所有烟雾相关寄存器均使用 MQ2 线性单位而非未经标定的 ppm。
        /// </summary>
        [Test]
        public void SmokeRegisters_UseMq2LinearUnitInsteadOfPpm()
        {
            RegisterDefinition[] smokeDefinitions = DeviceRegisterMap.All
                .Where(definition => definition.Name.Contains("烟雾", StringComparison.Ordinal))
                .ToArray();

            Assert.That(smokeDefinitions, Has.Length.EqualTo(5));
            Assert.That(smokeDefinitions, Has.All.Property(nameof(RegisterDefinition.Unit)).EqualTo("MQ2 线性单位"));
            Assert.That(smokeDefinitions, Has.None.Property(nameof(RegisterDefinition.Unit)).EqualTo("ppm"));
        }

        /// <summary>
        /// 表示测试代码独立固化的一项完整寄存器规格。
        /// </summary>
        /// <param name="DocumentAddress">面向用户的四万区文档地址。</param>
        /// <param name="ProtocolAddress">Modbus PDU 使用的零基协议地址。</param>
        /// <param name="Name">协议规定的寄存器中文名称。</param>
        /// <param name="RawEncoding">协议规定的原始线路字解释。</param>
        /// <param name="ExpectedRawMinimum">读取预期原始最小值。</param>
        /// <param name="ExpectedRawMaximum">读取预期原始最大值。</param>
        /// <param name="WritableRawMinimum">可写原始最小值；只读项为空。</param>
        /// <param name="WritableRawMaximum">可写原始最大值；只读项为空。</param>
        /// <param name="DisplayScale">原始整数到显示值的十进制乘数。</param>
        /// <param name="DecimalPlaces">界面显示的小数位数。</param>
        /// <param name="Unit">协议规定的显示单位或值域标签。</param>
        /// <param name="ValueDescription">枚举、位或设备特殊处理的关键含义。</param>
        private sealed record ExpectedRegisterSpecification(
            int DocumentAddress,
            ushort ProtocolAddress,
            string Name,
            RegisterRawEncoding RawEncoding,
            int ExpectedRawMinimum,
            int ExpectedRawMaximum,
            int? WritableRawMinimum,
            int? WritableRawMaximum,
            decimal DisplayScale,
            int DecimalPlaces,
            string Unit,
            string ValueDescription);
    }
}
