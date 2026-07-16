using CH32UpperComputer.Core.Registers;

namespace CH32UpperComputer.Core.Tests.Registers
{
    /// <summary>
    /// 验证综合继电器、活动报警位和报警使能位的独立建模与未知位诊断。
    /// </summary>
    public sealed class AlarmStateTests
    {
        /// <summary>
        /// 验证低六位严格映射为温度、烟雾、PM2.5、CO、CO2 和 SO2，且不产生额外通道。
        /// </summary>
        [Test]
        public void FromRegisterWords_LowSixBits_MapToTheSixDocumentedChannels()
        {
            AlarmState state = AlarmState.FromRegisterWords(1, 0x002D, 0x0033);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(state.Items, Has.Count.EqualTo(6));
                Assert.That(
                    state.Items.Select(item => item.Channel),
                    Is.EqualTo(new[]
                    {
                        AlarmChannel.Temperature,
                        AlarmChannel.Smoke,
                        AlarmChannel.Pm25,
                        AlarmChannel.CarbonMonoxide,
                        AlarmChannel.CarbonDioxide,
                        AlarmChannel.SulfurDioxide,
                    }));
                Assert.That(state.GetItem(AlarmChannel.Temperature).IsActive, Is.True);
                Assert.That(state.GetItem(AlarmChannel.Smoke).IsActive, Is.False);
                Assert.That(state.GetItem(AlarmChannel.Pm25).IsActive, Is.True);
                Assert.That(state.GetItem(AlarmChannel.CarbonMonoxide).IsActive, Is.True);
                Assert.That(state.GetItem(AlarmChannel.CarbonDioxide).IsActive, Is.False);
                Assert.That(state.GetItem(AlarmChannel.SulfurDioxide).IsActive, Is.True);
            }));
        }

        /// <summary>
        /// 验证报警使能与正在报警是两套独立事实，禁用通道的活动位不得被抹掉。
        /// </summary>
        [Test]
        public void FromRegisterWords_EnableAndActiveFlags_RemainIndependent()
        {
            AlarmState state = AlarmState.FromRegisterWords(1, 0x0001, 0x0002);
            AlarmItemState temperature = state.GetItem(AlarmChannel.Temperature);
            AlarmItemState smoke = state.GetItem(AlarmChannel.Smoke);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(temperature.IsActive, Is.True);
                Assert.That(temperature.IsEnabled, Is.False);
                Assert.That(smoke.IsActive, Is.False);
                Assert.That(smoke.IsEnabled, Is.True);
                Assert.That(state.CombinedRelayActive, Is.True);
            }));
        }

        /// <summary>
        /// 验证 40009 只把一解释为继电器报警，异常原始状态会保留并产生诊断。
        /// </summary>
        [Test]
        public void FromRegisterWords_CombinedRelay_PreservesUnexpectedRawWordWithDiagnostic()
        {
            AlarmState normal = AlarmState.FromRegisterWords(0, 0, 0);
            AlarmState alarm = AlarmState.FromRegisterWords(1, 0, 0);
            AlarmState unexpected = AlarmState.FromRegisterWords(2, 0, 0);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(normal.CombinedRelayActive, Is.False);
                Assert.That(alarm.CombinedRelayActive, Is.True);
                Assert.That(unexpected.CombinedRelayActive, Is.False);
                Assert.That(unexpected.CombinedRelayRawWord, Is.EqualTo(2));
                Assert.That(unexpected.Diagnostic, Does.Contain("40009"));
            }));
        }

        /// <summary>
        /// 验证活动位和使能位的未知高位被明确诊断，但模型始终只有文档规定的六个传感器。
        /// </summary>
        [Test]
        public void FromRegisterWords_UnknownHighBits_AreDiagnosedWithoutInventingChannels()
        {
            AlarmState state = AlarmState.FromRegisterWords(0, 0x8041, 0x4042);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(state.ActiveMask, Is.EqualTo(0x0001));
                Assert.That(state.EnableMask, Is.EqualTo(0x0002));
                Assert.That(state.UnknownActiveBits, Is.EqualTo(0x8040));
                Assert.That(state.UnknownEnableBits, Is.EqualTo(0x4040));
                Assert.That(state.Items, Has.Count.EqualTo(6));
                Assert.That(state.Diagnostic, Does.Contain("未知"));
                Assert.That(state.Diagnostic, Does.Contain("0x8040"));
                Assert.That(state.Diagnostic, Does.Contain("0x4040"));
            }));
        }
    }
}
