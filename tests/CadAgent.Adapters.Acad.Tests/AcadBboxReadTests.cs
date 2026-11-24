using CadAgent.Adapters.Acad;
using Xunit;

namespace CadAgent.Adapters.Acad.Tests;

/// <summary>
/// 切片A T10 真机核定收紧：GetBoundingBox by-ref 出参读取（无 COM/无真机，fake 目标反射调用）。
/// 根因实证（2026-11-25）：IDispatch 出参不标 by-ref ParameterModifier 时 COM 不回写——
/// 原实现传 double[3] 占位读回恒为全零假 bbox（handle-ops 真机首跑 before=0/after=0 即此根因）。
/// 修复形态与 spike phasea 一致（object[] 占位 + ParameterModifier[0..1]=true）。
/// </summary>
public class AcadBboxReadTests
{
    /// <summary>假实体：反射可见的 GetBoundingBox（by-ref object 出参，模拟 COM 回写面）。</summary>
    public sealed class FakeBboxTarget
    {
        public bool ReturnGarbage { get; init; }
        public bool Throw { get; init; }

        public void GetBoundingBox(ref object min, ref object max)
        {
            if (Throw) throw new InvalidOperationException("模拟无 GetBoundingBox 面");
            if (ReturnGarbage)
            {
                min = "not-a-vector";
                max = new double[2];
                return;
            }
            min = new double[] { 127749.9958, 120299.9999, 0 };
            max = new double[] { 127849.9958, 130099.9999, 3000 };
        }
    }

    [Fact]
    public void ReadBbox_ByRefOutParams_WriteBackRealValues()
    {
        double[]? bbox = AcadRuntime.ReadBbox(new FakeBboxTarget());
        Assert.NotNull(bbox);
        Assert.Equal(6, bbox!.Length);
        Assert.Equal(127749.9958, bbox[0]);
        Assert.Equal(120299.9999, bbox[1]);
        Assert.Equal(0, bbox[2]);
        Assert.Equal(127849.9958, bbox[3]);
        Assert.Equal(130099.9999, bbox[4]);
        Assert.Equal(3000, bbox[5]);
    }

    [Fact]
    public void BboxReader_MissingFaceHonestNull_NotZeroFake()
    {
        // 无此面→null（诚实降级），绝不以全零假 bbox 冒充。
        Assert.Null(AcadRuntime.BboxReader(new FakeBboxTarget { Throw = true }));
        Assert.Null(AcadRuntime.BboxReader(new FakeBboxTarget { ReturnGarbage = true }));
    }

    [Fact]
    public void ToVec3_RejectsNonVectorShapes()
    {
        Assert.Null(AcadRuntime.ToVec3(null));
        Assert.Null(AcadRuntime.ToVec3("abc"));
        Assert.Null(AcadRuntime.ToVec3(new double[2]));
        Assert.Null(AcadRuntime.ToVec3(new double[4]));
        Assert.Equal(new double[] { 1, 2, 3 }, AcadRuntime.ToVec3(new double[] { 1, 2, 3 }));
        Assert.Equal(new double[] { 1, 2, 3 }, AcadRuntime.ToVec3(new object[] { 1, 2.0, "3" }));
    }
}
