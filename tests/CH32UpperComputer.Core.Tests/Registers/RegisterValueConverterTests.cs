using CH32UpperComputer.Core.Protocol;
using CH32UpperComputer.Core.Registers;

namespace CH32UpperComputer.Core.Tests.Registers
{
    /// <summary>
    /// 验证逐寄存器原始字解释、精确缩放、写入边界和快照更新门控。
    /// </summary>
    public sealed class RegisterValueConverterTests
    {
        /// <summary>
        /// 测试内独立固化的 22 项可写寄存器地址、原始边界及缩放；不得从生产定义反向生成。
        /// </summary>
        private static readonly WritableRegisterSpecification[] FixedWritableSpecifications =
        [
            new(40001, 1, 64, 1m),
            new(40002, 0, 5, 1m),
            new(40011, -40, 100, 1m),
            new(40012, -32768, 0, 1m),
            new(40013, 0, 5000, 1m),
            new(40014, -32768, 0, 1m),
            new(40015, 0, 999, 1m),
            new(40016, -32768, 0, 1m),
            new(40017, 0, 1000, 1m),
            new(40018, -32768, 0, 1m),
            new(40019, 400, 5000, 1m),
            new(40020, -32768, 0, 1m),
            new(40021, 0, 20, 1m),
            new(40022, -32768, 0, 1m),
            new(40023, 0, 0x003F, 1m),
            new(40030, -10, 10, 1m),
            new(40031, -1000, 1000, 1m),
            new(40032, -200, 200, 1m),
            new(40033, -200, 200, 0.1m),
            new(40034, -1000, 1000, 1m),
            new(40035, -20, 20, 0.1m),
            new(40036, 1, 1, 1m),
        ];

        /// <summary>
        /// 验证温度、CO 和 SO2 使用各自定义的十进制缩放与显示精度。
        /// </summary>
        [Test]
        public void Decode_ScaledMeasurements_UsesPerRegisterDecimalScaleAndPrecision()
        {
            RegisterValue temperature = RegisterValueConverter.Decode(
                DeviceRegisterMap.GetByDocumentAddress(40003),
                0x09C4);
            RegisterValue carbonMonoxide = RegisterValueConverter.Decode(
                DeviceRegisterMap.GetByDocumentAddress(40006),
                0x007B);
            RegisterValue sulfurDioxide = RegisterValueConverter.Decode(
                DeviceRegisterMap.GetByDocumentAddress(40008),
                0x000F);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(temperature.DisplayValue, Is.EqualTo(25.00m));
                Assert.That(temperature.FormattedValue, Is.EqualTo("25.00"));
                Assert.That(carbonMonoxide.DisplayValue, Is.EqualTo(12.3m));
                Assert.That(carbonMonoxide.FormattedValue, Is.EqualTo("12.3"));
                Assert.That(sulfurDioxide.DisplayValue, Is.EqualTo(1.5m));
                Assert.That(sulfurDioxide.FormattedValue, Is.EqualTo("1.5"));
            }));
        }

        /// <summary>
        /// 验证烟雾值保持 MQ2 线性单位，不被错误描述为 ppm。
        /// </summary>
        [Test]
        public void Decode_SmokeMeasurement_PreservesMq2LinearUnit()
        {
            RegisterValue value = RegisterValueConverter.Decode(
                DeviceRegisterMap.GetByDocumentAddress(40004),
                1234);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(value.DisplayValue, Is.EqualTo(1234m));
                Assert.That(value.Definition.Unit, Is.EqualTo("MQ2 线性单位"));
                Assert.That(value.Definition.Unit, Is.Not.EqualTo("ppm"));
            }));
        }

        /// <summary>
        /// 验证有符号补偿值按二进制补码解释，而地址和波特率始终按 UInt16 解释。
        /// </summary>
        [Test]
        public void Decode_SignedCompensationAndUnsignedCodes_UsesDefinitionSpecificSignedness()
        {
            RegisterValue compensation = RegisterValueConverter.Decode(
                DeviceRegisterMap.GetByDocumentAddress(40033),
                unchecked((ushort)(short)-15));
            RegisterValue address = RegisterValueConverter.Decode(
                DeviceRegisterMap.GetByDocumentAddress(40001),
                0xFFFF);
            RegisterValue baudCode = RegisterValueConverter.Decode(
                DeviceRegisterMap.GetByDocumentAddress(40002),
                0x8000);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(compensation.InterpretedRawValue, Is.EqualTo(-15));
                Assert.That(compensation.DisplayValue, Is.EqualTo(-1.5m));
                Assert.That(address.InterpretedRawValue, Is.EqualTo(65535));
                Assert.That(baudCode.InterpretedRawValue, Is.EqualTo(32768));
            }));
        }

        /// <summary>
        /// 验证读取值越出预期范围时保留线路字和解释值，并生成诊断而不静默钳位。
        /// </summary>
        [Test]
        public void Decode_OutOfExpectedRange_PreservesValueAndReportsDiagnostic()
        {
            RegisterValue value = RegisterValueConverter.Decode(
                DeviceRegisterMap.GetByDocumentAddress(40024),
                0x7530);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(value.RawWord, Is.EqualTo(0x7530));
                Assert.That(value.InterpretedRawValue, Is.EqualTo(30000));
                Assert.That(value.DisplayValue, Is.EqualTo(300.00m));
                Assert.That(value.IsWithinExpectedRange, Is.False);
                Assert.That(value.Diagnostic, Does.Contain("超出"));
            }));
        }

        /// <summary>
        /// 验证只读寄存器拒绝进入写入编码流程。
        /// </summary>
        [Test]
        public void EncodeWrite_ReadOnlyRegister_IsRejected()
        {
            RegisterWriteResult result = RegisterValueConverter.EncodeWrite(
                DeviceRegisterMap.GetByDocumentAddress(40003),
                25m);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(result.IsSuccess, Is.False);
                Assert.That(result.RawWord, Is.Null);
                Assert.That(result.ErrorMessage, Does.Contain("只读"));
            }));
        }

        /// <summary>
        /// 验证生产寄存器表恰好包含测试内独立固化的 22 项可写定义及其精确原始边界和缩放。
        /// </summary>
        [Test]
        public void WritableDefinitions_MatchFixedTwentyTwoRegisterSpecifications()
        {
            RegisterDefinition[] actualWritableDefinitions = DeviceRegisterMap.All
                .Where(definition => definition.IsWritable)
                .ToArray();

            Assert.Multiple((Action)(() =>
            {
                Assert.That(FixedWritableSpecifications, Has.Length.EqualTo(22));
                Assert.That(actualWritableDefinitions, Has.Length.EqualTo(22));
                Assert.That(
                    actualWritableDefinitions.Select(definition => definition.DocumentAddress),
                    Is.EquivalentTo(FixedWritableSpecifications.Select(specification => specification.DocumentAddress)));
            }));

            foreach (WritableRegisterSpecification expected in FixedWritableSpecifications)
            {
                RegisterDefinition actual = DeviceRegisterMap.GetByDocumentAddress(expected.DocumentAddress);

                Assert.Multiple((Action)(() =>
                {
                    Assert.That(actual.IsWritable, Is.True, expected.DocumentAddress.ToString());
                    Assert.That(actual.WritableRawMinimum, Is.EqualTo(expected.MinimumRawValue), actual.Name);
                    Assert.That(actual.WritableRawMaximum, Is.EqualTo(expected.MaximumRawValue), actual.Name);
                    Assert.That(actual.DisplayScale, Is.EqualTo(expected.DisplayScale), actual.Name);
                }));
            }
        }

        /// <summary>
        /// 验证全部 22 项可写定义的精确最小值和最大值均可编码。
        /// </summary>
        /// <param name="documentAddress">测试内固化的寄存器文档地址。</param>
        /// <param name="minimumRawValue">测试内固化的原始最小值。</param>
        /// <param name="maximumRawValue">测试内固化的原始最大值。</param>
        /// <param name="displayScale">测试内固化的原始值到显示值缩放。</param>
        [TestCaseSource(nameof(WritableSpecificationCases))]
        public void EncodeWrite_EveryWritableDefinition_AcceptsBothBoundaries(
            int documentAddress,
            int minimumRawValue,
            int maximumRawValue,
            decimal displayScale)
        {
            RegisterDefinition definition = DeviceRegisterMap.GetByDocumentAddress(documentAddress);
            decimal minimumDisplay = minimumRawValue * displayScale;
            decimal maximumDisplay = maximumRawValue * displayScale;

            RegisterWriteResult minimumResult = RegisterValueConverter.EncodeWrite(definition, minimumDisplay);
            RegisterWriteResult maximumResult = RegisterValueConverter.EncodeWrite(definition, maximumDisplay);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(minimumResult.IsSuccess, Is.True, $"{definition.Name}: {minimumResult.ErrorMessage}");
                Assert.That(maximumResult.IsSuccess, Is.True, $"{definition.Name}: {maximumResult.ErrorMessage}");
                Assert.That(
                    RegisterValueConverter.InterpretRawWord(definition, minimumResult.RawWord!.Value),
                    Is.EqualTo(minimumRawValue));
                Assert.That(
                    RegisterValueConverter.InterpretRawWord(definition, maximumResult.RawWord!.Value),
                    Is.EqualTo(maximumRawValue));
            }));
        }

        /// <summary>
        /// 验证全部可写定义的边界外数值都被拒绝，不能在转换时溢出或钳位。
        /// </summary>
        /// <param name="documentAddress">测试内固化的寄存器文档地址。</param>
        /// <param name="minimumRawValue">测试内固化的原始最小值。</param>
        /// <param name="maximumRawValue">测试内固化的原始最大值。</param>
        /// <param name="displayScale">测试内固化的原始值到显示值缩放。</param>
        [TestCaseSource(nameof(WritableSpecificationCases))]
        public void EncodeWrite_EveryWritableDefinition_RejectsValuesOutsideBothBoundaries(
            int documentAddress,
            int minimumRawValue,
            int maximumRawValue,
            decimal displayScale)
        {
            RegisterDefinition definition = DeviceRegisterMap.GetByDocumentAddress(documentAddress);
            decimal belowMinimum = (minimumRawValue - 1m) * displayScale;
            decimal aboveMaximum = (maximumRawValue + 1m) * displayScale;

            RegisterWriteResult belowResult = RegisterValueConverter.EncodeWrite(definition, belowMinimum);
            RegisterWriteResult aboveResult = RegisterValueConverter.EncodeWrite(definition, aboveMaximum);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(belowResult.IsSuccess, Is.False, definition.Name);
                Assert.That(aboveResult.IsSuccess, Is.False, definition.Name);
                Assert.That(belowResult.RawWord, Is.Null, definition.Name);
                Assert.That(aboveResult.RawWord, Is.Null, definition.Name);
            }));
        }

        /// <summary>
        /// 验证六项报警回差均接受零且拒绝正一，明确锁定“只允许小于等于零”的特殊规则。
        /// </summary>
        [Test]
        public void EncodeWrite_AllHysteresisRegisters_AllowOnlyNonPositiveValues()
        {
            int[] hysteresisAddresses = [40012, 40014, 40016, 40018, 40020, 40022];

            foreach (int documentAddress in hysteresisAddresses)
            {
                RegisterDefinition definition = DeviceRegisterMap.GetByDocumentAddress(documentAddress);
                RegisterWriteResult zero = RegisterValueConverter.EncodeWrite(definition, 0m);
                RegisterWriteResult positiveOne = RegisterValueConverter.EncodeWrite(definition, 1m);

                Assert.Multiple((Action)(() =>
                {
                    Assert.That(zero.IsSuccess, Is.True, definition.Name);
                    Assert.That(positiveOne.IsSuccess, Is.False, definition.Name);
                }));
            }
        }

        /// <summary>
        /// 验证 40023 允许 0x0000 至 0x003F，且拒绝任何占用第六位以上的写入值。
        /// </summary>
        [Test]
        public void EncodeWrite_AlarmEnableMask_AllowsOnlyTheLowSixBits()
        {
            RegisterDefinition definition = DeviceRegisterMap.GetByDocumentAddress(40023);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(RegisterValueConverter.EncodeWrite(definition, 0m).IsSuccess, Is.True);
                Assert.That(RegisterValueConverter.EncodeWrite(definition, 63m).IsSuccess, Is.True);
                Assert.That(RegisterValueConverter.EncodeWrite(definition, 64m).IsSuccess, Is.False);
                Assert.That(RegisterValueConverter.EncodeWrite(definition, 65535m).IsSuccess, Is.False);
            }));
        }

        /// <summary>
        /// 验证 40036 只接受命令一，读取默认零、零写入和其他命令值均不能作为触发写入。
        /// </summary>
        [Test]
        public void EncodeWrite_FactoryResetCommand_AllowsOnlyOne()
        {
            RegisterDefinition definition = DeviceRegisterMap.GetByDocumentAddress(40036);
            RegisterValue readValue = RegisterValueConverter.Decode(definition, 0);
            RegisterWriteResult zero = RegisterValueConverter.EncodeWrite(definition, 0m);
            RegisterWriteResult one = RegisterValueConverter.EncodeWrite(definition, 1m);
            RegisterWriteResult two = RegisterValueConverter.EncodeWrite(definition, 2m);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(readValue.RawWord, Is.Zero);
                Assert.That(readValue.IsWithinExpectedRange, Is.True);
                Assert.That(zero.IsSuccess, Is.False);
                Assert.That(one.IsSuccess, Is.True, one.ErrorMessage);
                Assert.That(one.RawWord, Is.EqualTo(1));
                Assert.That(two.IsSuccess, Is.False);
            }));
        }

        /// <summary>
        /// 验证不能被寄存器精确缩放恢复为整数原始值的输入被拒绝。
        /// </summary>
        [Test]
        public void EncodeWrite_NonIntegralRawValue_IsRejected()
        {
            RegisterWriteResult result = RegisterValueConverter.EncodeWrite(
                DeviceRegisterMap.GetByDocumentAddress(40033),
                1.25m);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("步进"));
        }

        /// <summary>
        /// 验证快照只接受成功、结构匹配且覆盖已知寄存器的 0x03 解析响应。
        /// </summary>
        [Test]
        public void SnapshotApply_ValidParsedReadResponse_UpdatesKnownValuesWithCallerTimestamp()
        {
            ModbusRequest request = ModbusRequestFactory.CreateReadHoldingRegisters(1, 0x0002, 2);
            ModbusResponse response = ParseReadResponse(request, 2500, 1234);
            DateTimeOffset timestamp = new(2026, 7, 16, 12, 30, 0, TimeSpan.FromHours(8));
            DeviceSnapshot snapshot = new();

            SnapshotApplyResult result = snapshot.Apply(request, response, timestamp);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
                Assert.That(result.UpdatedRegisterCount, Is.EqualTo(2));
                Assert.That(snapshot.LastUpdatedAt, Is.EqualTo(timestamp));
                Assert.That(snapshot.GetByProtocolAddress(0x0002).DisplayValue, Is.EqualTo(25.00m));
                Assert.That(snapshot.GetByProtocolAddress(0x0003).DisplayValue, Is.EqualTo(1234m));
            }));
        }

        /// <summary>
        /// 验证协议错误和 Modbus 异常响应均不能改变快照。
        /// </summary>
        [Test]
        public void SnapshotApply_UnsuccessfulParsedResponses_AreRejectedWithoutMutation()
        {
            ModbusRequest request = ModbusRequestFactory.CreateReadHoldingRegisters(1, 0x0002, 1);
            ModbusResponse protocolError = ModbusResponseParser.Parse(request, [0x01, 0x03, 0x00]);
            ModbusResponse exception = ModbusResponseParser.Parse(
                request,
                ModbusCrc16.Append([0x01, 0x83, 0x02]));
            DeviceSnapshot snapshot = new();

            SnapshotApplyResult protocolResult = snapshot.Apply(request, protocolError, DateTimeOffset.UnixEpoch);
            SnapshotApplyResult exceptionResult = snapshot.Apply(request, exception, DateTimeOffset.UnixEpoch);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(protocolResult.IsSuccess, Is.False);
                Assert.That(exceptionResult.IsSuccess, Is.False);
                Assert.That(snapshot.Values, Is.Empty);
                Assert.That(snapshot.LastUpdatedAt, Is.Null);
            }));
        }

        /// <summary>
        /// 验证即使响应本身成功，传入不同事务请求或未知寄存器块也必须整体拒绝。
        /// </summary>
        [Test]
        public void SnapshotApply_MismatchedTransactionOrUnknownRange_IsRejectedAtomically()
        {
            ModbusRequest sourceRequest = ModbusRequestFactory.CreateReadHoldingRegisters(1, 0x0002, 2);
            ModbusResponse sourceResponse = ParseReadResponse(sourceRequest, 2500, 1234);
            ModbusRequest mismatchedRequest = ModbusRequestFactory.CreateReadHoldingRegisters(1, 0x0002, 1);
            ModbusRequest unknownRangeRequest = ModbusRequestFactory.CreateReadHoldingRegisters(1, 0x0023, 2);
            ModbusResponse unknownRangeResponse = ParseReadResponse(unknownRangeRequest, 0, 0);
            DeviceSnapshot snapshot = new();

            SnapshotApplyResult mismatchResult = snapshot.Apply(
                mismatchedRequest,
                sourceResponse,
                DateTimeOffset.UnixEpoch);
            SnapshotApplyResult unknownResult = snapshot.Apply(
                unknownRangeRequest,
                unknownRangeResponse,
                DateTimeOffset.UnixEpoch);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(mismatchResult.IsSuccess, Is.False);
                Assert.That(unknownResult.IsSuccess, Is.False);
                Assert.That(snapshot.Values, Is.Empty);
                Assert.That(snapshot.LastUpdatedAt, Is.Null);
            }));
        }

        /// <summary>
        /// 验证成功响应只能由实际参与解析的同一请求写入快照，不能更换为同地址、同功能、同数量但不同起点的请求。
        /// </summary>
        [Test]
        public void SnapshotApply_SameShapeButDifferentOriginatingRequest_IsRejected()
        {
            ModbusRequest sourceRequest = ModbusRequestFactory.CreateReadHoldingRegisters(1, 0x0002, 1);
            ModbusResponse sourceResponse = ParseReadResponse(sourceRequest, 2500);
            ModbusRequest otherRequest = ModbusRequestFactory.CreateReadHoldingRegisters(1, 0x0003, 1);
            DeviceSnapshot snapshot = new();

            SnapshotApplyResult result = snapshot.Apply(
                otherRequest,
                sourceResponse,
                DateTimeOffset.UnixEpoch);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(result.IsSuccess, Is.False);
                Assert.That(result.ErrorMessage, Does.Contain("解析"));
                Assert.That(snapshot.Values, Is.Empty);
            }));
        }

        /// <summary>
        /// 验证 0xFE 未知地址查询仅用于发现真实从站地址，不能把返回的地址值误写为 40001 快照。
        /// </summary>
        [Test]
        public void SnapshotApply_UnknownAddressDiscoveryResponse_IsRejectedWithoutMutation()
        {
            ModbusRequest request = ModbusRequestFactory.CreateUnknownAddressQuery();
            byte[] frame = ModbusCrc16.Append([0x05, 0x03, 0x02, 0x00, 0x05]);
            ModbusResponse response = ModbusResponseParser.Parse(request, frame);
            DeviceSnapshot snapshot = new();

            SnapshotApplyResult result = snapshot.Apply(
                request,
                response,
                DateTimeOffset.UnixEpoch);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(response.IsSuccess, Is.True, response.ErrorMessage);
                Assert.That(result.IsSuccess, Is.False);
                Assert.That(result.ErrorMessage, Does.Contain("未知地址"));
                Assert.That(snapshot.Values, Is.Empty);
                Assert.That(snapshot.LastUpdatedAt, Is.Null);
            }));
        }

        /// <summary>
        /// 验证有效响应中的越界测量值仍会作为带诊断的原始事实进入快照。
        /// </summary>
        [Test]
        public void SnapshotApply_OutOfRangeMeasurement_PreservesDiagnosticValue()
        {
            ModbusRequest request = ModbusRequestFactory.CreateReadHoldingRegisters(1, 0x0017, 1);
            ModbusResponse response = ParseReadResponse(request, 30000);
            DeviceSnapshot snapshot = new();

            SnapshotApplyResult result = snapshot.Apply(request, response, DateTimeOffset.UnixEpoch);
            RegisterValue value = snapshot.GetByProtocolAddress(0x0017);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
                Assert.That(value.RawWord, Is.EqualTo(30000));
                Assert.That(value.DisplayValue, Is.EqualTo(300m));
                Assert.That(value.IsWithinExpectedRange, Is.False);
                Assert.That(value.Diagnostic, Is.Not.Empty);
            }));
        }

        /// <summary>
        /// 为 NUnit 构造由独立固定规格驱动的 22 项可写寄存器测试用例。
        /// </summary>
        /// <returns>每项仅携带测试内固定的地址、原始边界及缩放，不读取生产定义。</returns>
        private static IEnumerable<TestCaseData> WritableSpecificationCases()
        {
            foreach (WritableRegisterSpecification specification in FixedWritableSpecifications)
            {
                yield return new TestCaseData(
                    specification.DocumentAddress,
                    specification.MinimumRawValue,
                    specification.MaximumRawValue,
                    specification.DisplayScale)
                    .SetName($"{{m}}({specification.DocumentAddress})");
            }
        }

        private static ModbusResponse ParseReadResponse(
            ModbusRequest request,
            params ushort[] values)
        {
            byte[] body = new byte[3 + (values.Length * 2)];
            body[0] = request.SlaveAddress;
            body[1] = (byte)ModbusFunctionCode.ReadHoldingRegisters;
            body[2] = checked((byte)(values.Length * 2));

            for (int index = 0; index < values.Length; index++)
            {
                body[3 + (index * 2)] = (byte)(values[index] >> 8);
                body[4 + (index * 2)] = (byte)(values[index] & 0x00FF);
            }

            return ModbusResponseParser.Parse(request, ModbusCrc16.Append(body));
        }

        /// <summary>
        /// 表示测试代码独立固化的一项可写寄存器边界规格。
        /// </summary>
        /// <param name="DocumentAddress">面向用户的四万区文档地址。</param>
        /// <param name="MinimumRawValue">协议允许写入的原始最小值。</param>
        /// <param name="MaximumRawValue">协议允许写入的原始最大值。</param>
        /// <param name="DisplayScale">原始整数到界面显示值的十进制乘数。</param>
        private sealed record WritableRegisterSpecification(
            int DocumentAddress,
            int MinimumRawValue,
            int MaximumRawValue,
            decimal DisplayScale);
    }
}
