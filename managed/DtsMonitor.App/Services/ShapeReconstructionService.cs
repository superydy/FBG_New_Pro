using DtsMonitor.App.Models;

namespace DtsMonitor.App.Services;

public static class ShapeReconstructionService
{
    public static ShapeReconstructionResult Reconstruct2D(
        SnapshotModel snapshot,
        ShapeSensingProfile? profile,
        ShapeReconstructionSettings settings,
        float[]? referenceTop,
        float[]? referenceBottom)
    {
        if (settings.Mode == ShapeSensingMode.SingleFiber)
        {
            return ReconstructSingleFiber2D(snapshot, profile, settings, referenceTop);
        }

        return ReconstructDualFiber2D(snapshot, profile, settings, referenceTop, referenceBottom);
    }

    private static ShapeReconstructionResult ReconstructDualFiber2D(
        SnapshotModel snapshot,
        ShapeSensingProfile? profile,
        ShapeReconstructionSettings settings,
        float[]? referenceTop,
        float[]? referenceBottom)
    {
        float[] wavelengths = snapshot.SensorWavelengthsNm;
        if (wavelengths.Length < 4)
        {
            return Invalid(snapshot, "实时波长点不足，无法重构形状。");
        }

        int start = Math.Clamp(settings.StartIndex, 0, wavelengths.Length - 1);
        int end = Math.Clamp(settings.EndIndex == int.MaxValue ? wavelengths.Length - 1 : settings.EndIndex, start, wavelengths.Length - 1);
        int availableCount = end - start + 1;
        if (availableCount < 4)
        {
            return Invalid(snapshot, "索引范围内的 FBG 点不足。");
        }

        DualFiberPairing pairing = BuildDualFiberPairing(profile, snapshot, settings, start, availableCount);
        int pairCount = pairing.PairCount;
        if (pairCount < 2)
        {
            return Invalid(snapshot, "上下光栅配对数量不足。");
        }

        float[] topAfter = new float[pairCount];
        float[] bottomAfter = new float[pairCount];
        float[] topBefore = new float[pairCount];
        float[] bottomBefore = new float[pairCount];
        float[] sensorPositions = pairing.PairPositionsM;

        for (int i = 0; i < pairCount; i++)
        {
            int topIndex = pairing.TopIndices[i];
            int bottomIndex = pairing.BottomIndices[i];
            topAfter[i] = GetFiniteOrNaN(wavelengths, topIndex);
            bottomAfter[i] = GetFiniteOrNaN(wavelengths, bottomIndex);
            topBefore[i] = ResolveReference(profile, referenceTop, topIndex, i, topAfter[i]);
            bottomBefore[i] = ResolveReference(profile, referenceBottom, bottomIndex, i, bottomAfter[i]);
        }

        float peDenominator = 1f - settings.PhotoElasticCoefficient;
        float gratingDistance = Math.Abs(settings.GratingDistanceM) > 0.000001f ? settings.GratingDistanceM : 0.003f;
        float[] strainTop = new float[pairCount];
        float[] strainBottom = new float[pairCount];
        float[] curvature = new float[pairCount];
        for (int i = 0; i < pairCount; i++)
        {
            if (!IsUsableWavelength(topBefore[i], topAfter[i]) || !IsUsableWavelength(bottomBefore[i], bottomAfter[i]))
            {
                strainTop[i] = float.NaN;
                strainBottom[i] = float.NaN;
                curvature[i] = float.NaN;
                continue;
            }

            strainTop[i] = (topAfter[i] - topBefore[i]) / (topBefore[i] * peDenominator);
            strainBottom[i] = (bottomAfter[i] - bottomBefore[i]) / (bottomBefore[i] * peDenominator);
            curvature[i] = (strainBottom[i] - strainTop[i]) / gratingDistance;
        }

        RemoveMean(curvature);
        float[] smoothCurvature = MovingAverage(curvature, settings.SmoothWindow);
        float step = settings.FineStepM > 0.0001f ? settings.FineStepM : 0.005f;
        InterpolatePchip(sensorPositions, smoothCurvature, step, out float[] arcFine, out float[] curvatureFine);
        if (arcFine.Length < 2)
        {
            return Invalid(snapshot, "曲率插值失败。");
        }

        IntegrateShapeCircularArc(arcFine, curvatureFine, 0f, out float[] x, out float[] y);
        float maxDeflection = y.Where(float.IsFinite).Select(Math.Abs).DefaultIfEmpty(0f).Max();
        return new ShapeReconstructionResult
        {
            TimestampMs = snapshot.TimestampMs,
            Timestamp = snapshot.Timestamp,
            Channel = snapshot.Channel,
            IsValid = true,
            StatusText = "形状重构正常",
            ArcPositionsM = arcFine,
            ShapeX = x,
            ShapeY = y,
            SensorArcPositionsM = sensorPositions,
            Curvature = curvatureFine,
            StrainTopMicro = strainTop.Select(v => v * 1_000_000f).ToArray(),
            StrainBottomMicro = strainBottom.Select(v => v * 1_000_000f).ToArray(),
            MaxDeflectionM = maxDeflection,
            PairCount = pairCount,
            Mode = ShapeSensingMode.DualFiber
        };
    }

    private static ShapeReconstructionResult ReconstructSingleFiber2D(
        SnapshotModel snapshot,
        ShapeSensingProfile? profile,
        ShapeReconstructionSettings settings,
        float[]? referenceWavelengths)
    {
        float[] wavelengths = snapshot.SensorWavelengthsNm;
        if (wavelengths.Length < 2)
        {
            return Invalid(snapshot, "单光纤模式实时波长点不足，无法重构形状。");
        }

        int start = Math.Clamp(settings.StartIndex, 0, wavelengths.Length - 1);
        int end = Math.Clamp(settings.EndIndex == int.MaxValue ? wavelengths.Length - 1 : settings.EndIndex, start, wavelengths.Length - 1);
        int sensorCount = end - start + 1;
        if (sensorCount < 2)
        {
            return Invalid(snapshot, "单光纤模式索引范围内的 FBG 点不足。");
        }

        float peDenominator = 1f - settings.PhotoElasticCoefficient;
        float neutralAxisDistance = Math.Abs(settings.NeutralAxisDistanceM) > 0.000001f
            ? settings.NeutralAxisDistanceM
            : 0.003f;
        float[] strain = new float[sensorCount];
        float[] positions = ResolveSingleFiberSensorPositions(profile, snapshot, start, sensorCount);
        for (int i = 0; i < sensorCount; i++)
        {
            int rawIndex = start + i;
            float after = GetFiniteOrNaN(wavelengths, rawIndex);
            float before = ResolveReference(profile, referenceWavelengths, rawIndex, i, after);
            strain[i] = IsUsableWavelength(before, after)
                ? (after - before) / (before * peDenominator)
                : float.NaN;
        }

        float[] curvature = new float[sensorCount];
        float[] finiteStrain = strain.Where(float.IsFinite).ToArray();
        float meanStrain = finiteStrain.Length > 0 ? finiteStrain.Average() : 0f;
        for (int i = 0; i < sensorCount; i++)
        {
            curvature[i] = float.IsFinite(strain[i])
                ? (strain[i] - meanStrain) / neutralAxisDistance
                : float.NaN;
        }

        RemoveMean(curvature);
        float[] smoothCurvature = MovingAverage(curvature, settings.SmoothWindow);
        float step = settings.FineStepM > 0.0001f ? settings.FineStepM : 0.005f;
        InterpolatePchip(positions, smoothCurvature, step, out float[] arcFine, out float[] curvatureFine);
        if (arcFine.Length < 2)
        {
            return Invalid(snapshot, "单光纤模式曲率插值失败。");
        }

        IntegrateShapeCircularArc(arcFine, curvatureFine, 0f, out float[] x, out float[] y);
        float maxDeflection = y.Where(float.IsFinite).Select(Math.Abs).DefaultIfEmpty(0f).Max();
        float[] strainMicro = strain.Select(v => v * 1_000_000f).ToArray();
        return new ShapeReconstructionResult
        {
            TimestampMs = snapshot.TimestampMs,
            Timestamp = snapshot.Timestamp,
            Channel = snapshot.Channel,
            IsValid = true,
            StatusText = "单光纤近似形状重构正常",
            ArcPositionsM = arcFine,
            ShapeX = x,
            ShapeY = y,
            SensorArcPositionsM = positions,
            Curvature = curvatureFine,
            StrainTopMicro = strainMicro,
            StrainBottomMicro = strainMicro,
            MaxDeflectionM = maxDeflection,
            PairCount = sensorCount,
            Mode = ShapeSensingMode.SingleFiber
        };
    }

    private static ShapeReconstructionResult Invalid(SnapshotModel snapshot, string status) => new()
    {
        TimestampMs = snapshot.TimestampMs,
        Timestamp = snapshot.Timestamp,
        Channel = snapshot.Channel,
        IsValid = false,
        StatusText = status
    };

    private static DualFiberPairing BuildDualFiberPairing(
        ShapeSensingProfile? profile,
        SnapshotModel snapshot,
        ShapeReconstructionSettings settings,
        int start,
        int availableCount)
    {
        float[] positions = profile?.SensorPositionsM.Length > 0
            ? profile.SensorPositionsM
            : snapshot.SensorPositionsM;

        int offset = settings.PairOffset > 0 ? settings.PairOffset : availableCount / 2;
        int halfPairCount = Math.Min(offset, availableCount - offset);
        if (halfPairCount >= 2 && ShouldUseHalfSplitPairing(positions, start, offset, halfPairCount))
        {
            int[] top = new int[halfPairCount];
            int[] bottom = new int[halfPairCount];
            for (int i = 0; i < halfPairCount; i++)
            {
                top[i] = start + i;
                bottom[i] = start + offset + i;
            }

            return new DualFiberPairing(top, bottom, ResolvePairPositions(positions, top, bottom));
        }

        int adjacentPairCount = availableCount / 2;
        int[] adjacentTop = new int[adjacentPairCount];
        int[] adjacentBottom = new int[adjacentPairCount];
        for (int i = 0; i < adjacentPairCount; i++)
        {
            adjacentTop[i] = start + i * 2;
            adjacentBottom[i] = start + i * 2 + 1;
        }

        return new DualFiberPairing(adjacentTop, adjacentBottom, ResolvePairPositions(positions, adjacentTop, adjacentBottom));
    }

    private static bool ShouldUseHalfSplitPairing(float[] positions, int start, int offset, int pairCount)
    {
        if (pairCount < 2 || positions.Length <= start + offset)
        {
            return false;
        }

        if (!TryGetRange(positions, start, pairCount, out float topMin, out float topMax) ||
            !TryGetRange(positions, start + offset, pairCount, out float bottomMin, out float bottomMax))
        {
            return true;
        }

        float topSpan = topMax - topMin;
        float bottomSpan = bottomMax - bottomMin;
        if (topSpan <= 0f || bottomSpan <= 0f)
        {
            return false;
        }

        float overlap = Math.Min(topMax, bottomMax) - Math.Max(topMin, bottomMin);
        return overlap >= Math.Min(topSpan, bottomSpan) * 0.5f;
    }

    private static bool TryGetRange(float[] positions, int start, int count, out float min, out float max)
    {
        min = float.PositiveInfinity;
        max = float.NegativeInfinity;
        int end = Math.Min(positions.Length, start + count);
        for (int i = start; i < end; i++)
        {
            float position = positions[i];
            if (!float.IsFinite(position))
            {
                continue;
            }

            min = Math.Min(min, position);
            max = Math.Max(max, position);
        }

        return float.IsFinite(min) && float.IsFinite(max);
    }

    private static float[] ResolvePairPositions(float[] positions, int[] topIndices, int[] bottomIndices)
    {
        int pairCount = Math.Min(topIndices.Length, bottomIndices.Length);
        float[] result = new float[pairCount];
        for (int i = 0; i < pairCount; i++)
        {
            int topIndex = topIndices[i];
            int bottomIndex = bottomIndices[i];
            bool hasTop = topIndex < positions.Length && float.IsFinite(positions[topIndex]);
            bool hasBottom = bottomIndex < positions.Length && float.IsFinite(positions[bottomIndex]);
            result[i] = hasTop && hasBottom
                ? 0.5f * (positions[topIndex] + positions[bottomIndex])
                : hasTop
                    ? positions[topIndex]
                    : hasBottom
                        ? positions[bottomIndex]
                        : i;
        }

        EnsureIncreasingPositions(result);
        return result;
    }

    private readonly record struct DualFiberPairing(int[] TopIndices, int[] BottomIndices, float[] PairPositionsM)
    {
        public int PairCount => Math.Min(TopIndices.Length, BottomIndices.Length);
    }

    private static float[] ResolveSensorPositions(ShapeSensingProfile? profile, SnapshotModel snapshot, int start, int pairCount)
    {
        float[] positions = profile?.SensorPositionsM.Length > 0
            ? profile.SensorPositionsM
            : snapshot.SensorPositionsM;

        float[] result = new float[pairCount];
        if (TryBuildPairPositionsFromFullRange(positions, pairCount, result))
        {
            EnsureIncreasingPositions(result);
            return result;
        }

        for (int i = 0; i < pairCount; i++)
        {
            int sourceIndex = start + i;
            result[i] = sourceIndex < positions.Length && float.IsFinite(positions[sourceIndex])
                ? positions[sourceIndex]
                : i;
        }

        EnsureIncreasingPositions(result);
        return result;
    }

    private static bool TryBuildPairPositionsFromFullRange(float[] positions, int pairCount, float[] result)
    {
        if (pairCount <= 1 || positions.Length < pairCount * 2)
        {
            return false;
        }

        float[] validPositions = positions
            .Where(float.IsFinite)
            .OrderBy(x => x)
            .ToArray();
        if (validPositions.Length < 2)
        {
            return false;
        }

        float min = validPositions[0];
        float max = validPositions[^1];
        if (max <= min)
        {
            return false;
        }

        float firstHalfMax = positions
            .Take(pairCount)
            .Where(float.IsFinite)
            .DefaultIfEmpty(min)
            .Max();

        // When coefficients contain both sensing rows but pairing halves the count, the
        // first half can cover only half the physical probe. Use the full calibration span.
        if (firstHalfMax >= max * 0.85f)
        {
            return false;
        }

        float step = (max - min) / (pairCount - 1);
        for (int i = 0; i < pairCount; i++)
        {
            result[i] = min + step * i;
        }

        return true;
    }

    private static float[] ResolveSingleFiberSensorPositions(ShapeSensingProfile? profile, SnapshotModel snapshot, int start, int sensorCount)
    {
        float[] positions = profile?.SensorPositionsM.Length > 0
            ? profile.SensorPositionsM
            : snapshot.SensorPositionsM;

        float[] result = new float[sensorCount];
        for (int i = 0; i < sensorCount; i++)
        {
            int sourceIndex = start + i;
            result[i] = sourceIndex < positions.Length && float.IsFinite(positions[sourceIndex])
                ? positions[sourceIndex]
                : i;
        }

        EnsureIncreasingPositions(result);
        return result;
    }

    private static void EnsureIncreasingPositions(float[] positions)
    {
        if (positions.Length == 0)
        {
            return;
        }

        for (int i = 0; i < positions.Length; i++)
        {
            if (i > 0 && positions[i] <= positions[i - 1])
            {
                positions[i] = positions[i - 1] + 1f;
            }
        }
    }

    private static float ResolveReference(ShapeSensingProfile? profile, float[]? savedReference, int rawIndex, int pairIndex, float current)
    {
        if (savedReference is not null && pairIndex < savedReference.Length && IsUsableWavelength(savedReference[pairIndex], current))
        {
            return savedReference[pairIndex];
        }

        if (profile is not null &&
            rawIndex < profile.ReferenceStrainWavelengthsNm.Length &&
            IsUsableWavelength(profile.ReferenceStrainWavelengthsNm[rawIndex], current))
        {
            return profile.ReferenceStrainWavelengthsNm[rawIndex];
        }

        return current;
    }

    private static float GetFiniteOrNaN(float[] values, int index)
    {
        return index >= 0 && index < values.Length && float.IsFinite(values[index])
            ? values[index]
            : float.NaN;
    }

    private static bool IsUsableWavelength(float before, float after)
    {
        return float.IsFinite(before) && before > 0f && float.IsFinite(after) && after > 0f;
    }

    private static void RemoveMean(float[] values)
    {
        float[] finite = values.Where(float.IsFinite).ToArray();
        if (finite.Length == 0)
        {
            return;
        }

        float mean = finite.Average();
        for (int i = 0; i < values.Length; i++)
        {
            if (float.IsFinite(values[i]))
            {
                values[i] -= mean;
            }
        }
    }

    private static float[] MovingAverage(float[] values, int window)
    {
        int count = values.Length;
        float[] result = new float[count];
        if (count == 0)
        {
            return result;
        }

        int normalizedWindow = Math.Max(1, window);
        for (int i = 0; i < count; i++)
        {
            int start = Math.Max(0, i - normalizedWindow + 1);
            int end = Math.Min(count, i + normalizedWindow);
            double sum = 0d;
            int used = 0;
            for (int j = start; j < end; j++)
            {
                if (!float.IsFinite(values[j]))
                {
                    continue;
                }

                sum += values[j];
                used++;
            }

            result[i] = used > 0 ? (float)(sum / used) : float.NaN;
        }

        return result;
    }

    private static float[] SmoothSavitzkyGolay(float[] values, int window)
    {
        int normalizedWindow = Math.Max(1, window);
        if (normalizedWindow % 2 == 0)
        {
            normalizedWindow++;
        }

        float[] result = new float[values.Length];
        if (values.Length < 3)
        {
            Array.Copy(values, result, values.Length);
            return result;
        }

        int radius = normalizedWindow / 2;
        for (int i = 0; i < values.Length; i++)
        {
            double s0 = 0d;
            double s1 = 0d;
            double s2 = 0d;
            double s3 = 0d;
            double s4 = 0d;
            double b0 = 0d;
            double b1 = 0d;
            double b2 = 0d;
            for (int j = Math.Max(0, i - radius); j <= Math.Min(values.Length - 1, i + radius); j++)
            {
                if (!float.IsFinite(values[j]))
                {
                    continue;
                }

                double t = j - i;
                double t2 = t * t;
                s0 += 1d;
                s1 += t;
                s2 += t2;
                s3 += t2 * t;
                s4 += t2 * t2;
                b0 += values[j];
                b1 += values[j] * t;
                b2 += values[j] * t2;
            }

            result[i] = s0 >= 3d && TrySolve3x3(
                s0, s1, s2,
                s1, s2, s3,
                s2, s3, s4,
                b0, b1, b2,
                out double a0,
                out _,
                out _)
                ? (float)a0
                : values[i];
        }

        return result;
    }

    private static bool TrySolve3x3(
        double a00, double a01, double a02,
        double a10, double a11, double a12,
        double a20, double a21, double a22,
        double b0, double b1, double b2,
        out double x0,
        out double x1,
        out double x2)
    {
        double det =
            a00 * (a11 * a22 - a12 * a21) -
            a01 * (a10 * a22 - a12 * a20) +
            a02 * (a10 * a21 - a11 * a20);
        if (Math.Abs(det) < 1e-12d)
        {
            x0 = x1 = x2 = 0d;
            return false;
        }

        x0 = (
            b0 * (a11 * a22 - a12 * a21) -
            a01 * (b1 * a22 - a12 * b2) +
            a02 * (b1 * a21 - a11 * b2)) / det;
        x1 = (
            a00 * (b1 * a22 - a12 * b2) -
            b0 * (a10 * a22 - a12 * a20) +
            a02 * (a10 * b2 - b1 * a20)) / det;
        x2 = (
            a00 * (a11 * b2 - b1 * a21) -
            a01 * (a10 * b2 - b1 * a20) +
            b0 * (a10 * a21 - a11 * a20)) / det;
        return true;
    }

    private static void InterpolatePchip(float[] positions, float[] values, float step, out float[] finePositions, out float[] fineValues)
    {
        var valid = positions
            .Zip(values, (s, k) => new { S = s, K = k })
            .Where(x => float.IsFinite(x.S) && float.IsFinite(x.K))
            .OrderBy(x => x.S)
            .ToArray();

        if (valid.Length < 2)
        {
            finePositions = Array.Empty<float>();
            fineValues = Array.Empty<float>();
            return;
        }

        float start = valid[0].S;
        float end = valid[^1].S;
        int count = Math.Max(2, (int)MathF.Floor((end - start) / step) + 1);
        finePositions = new float[count];
        fineValues = new float[count];
        float[] x = valid.Select(v => v.S).ToArray();
        float[] y = valid.Select(v => v.K).ToArray();
        float[] derivatives = BuildPchipDerivatives(x, y);
        int segment = 0;
        for (int i = 0; i < count; i++)
        {
            float s = i == count - 1 ? end : start + i * step;
            while (segment < x.Length - 2 && x[segment + 1] < s)
            {
                segment++;
            }

            float s0 = x[segment];
            float s1 = x[Math.Min(segment + 1, x.Length - 1)];
            float h = s1 - s0;
            float t = h > 0f ? (s - s0) / h : 0f;
            float t2 = t * t;
            float t3 = t2 * t;
            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;
            finePositions[i] = s;
            fineValues[i] =
                h00 * y[segment] +
                h10 * h * derivatives[segment] +
                h01 * y[Math.Min(segment + 1, y.Length - 1)] +
                h11 * h * derivatives[Math.Min(segment + 1, derivatives.Length - 1)];
        }
    }

    private static float[] BuildPchipDerivatives(float[] x, float[] y)
    {
        int n = x.Length;
        float[] derivatives = new float[n];
        if (n == 2)
        {
            float slope = SafeSlope(x[0], x[1], y[0], y[1]);
            derivatives[0] = slope;
            derivatives[1] = slope;
            return derivatives;
        }

        float[] h = new float[n - 1];
        float[] delta = new float[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            h[i] = Math.Max(0.000001f, x[i + 1] - x[i]);
            delta[i] = (y[i + 1] - y[i]) / h[i];
        }

        derivatives[0] = PchipEndpointDerivative(h[0], h[1], delta[0], delta[1]);
        derivatives[^1] = PchipEndpointDerivative(h[^1], h[^2], delta[^1], delta[^2]);
        for (int i = 1; i < n - 1; i++)
        {
            if (delta[i - 1] == 0f || delta[i] == 0f || MathF.Sign(delta[i - 1]) != MathF.Sign(delta[i]))
            {
                derivatives[i] = 0f;
                continue;
            }

            float w1 = 2f * h[i] + h[i - 1];
            float w2 = h[i] + 2f * h[i - 1];
            derivatives[i] = (w1 + w2) / (w1 / delta[i - 1] + w2 / delta[i]);
        }

        return derivatives;
    }

    private static float PchipEndpointDerivative(float h0, float h1, float delta0, float delta1)
    {
        float derivative = ((2f * h0 + h1) * delta0 - h0 * delta1) / (h0 + h1);
        if (MathF.Sign(derivative) != MathF.Sign(delta0))
        {
            return 0f;
        }

        if (MathF.Sign(delta0) != MathF.Sign(delta1) && MathF.Abs(derivative) > MathF.Abs(3f * delta0))
        {
            return 3f * delta0;
        }

        return derivative;
    }

    private static float SafeSlope(float x0, float x1, float y0, float y1)
    {
        float h = x1 - x0;
        return Math.Abs(h) > 0.000001f ? (y1 - y0) / h : 0f;
    }

    private static void IntegrateShapeEkf(float[] arcPositions, float[] curvature, out float[] x, out float[] y)
    {
        x = new float[arcPositions.Length];
        y = new float[arcPositions.Length];
        float[] beta = new float[arcPositions.Length];
        double p00 = 1d;
        double p01 = 0d;
        double p10 = 0d;
        double p11 = 1d;
        const double processNoiseY = 0.001d;
        const double processNoiseBeta = 0.01d;
        const double measurementNoise = 0.1d;

        for (int i = 1; i < arcPositions.Length; i++)
        {
            float ds = Math.Max(0.000001f, arcPositions[i] - arcPositions[i - 1]);
            float kappaPrevious = float.IsFinite(curvature[i - 1]) ? curvature[i - 1] : 0f;
            float kappaCurrent = float.IsFinite(curvature[i]) ? curvature[i] : kappaPrevious;
            double betaPrevious = beta[i - 1];

            double predictedY = y[i - 1] + Math.Sin(betaPrevious) * ds;
            double predictedBeta = betaPrevious + kappaPrevious * ds;

            double f01 = Math.Cos(betaPrevious) * ds;
            double pp00 = p00 + f01 * p10 + (p01 + f01 * p11) * f01 + processNoiseY;
            double pp01 = p01 + f01 * p11;
            double pp10 = p10 + p11 * f01;
            double pp11 = p11 + processNoiseBeta;

            double h1 = 1d / ds;
            double innovation = kappaCurrent - ((predictedBeta - betaPrevious) / ds);
            double s = h1 * (pp11 * h1) + measurementNoise;
            double k0 = pp01 * h1 / s;
            double k1 = pp11 * h1 / s;

            y[i] = (float)(predictedY + k0 * innovation);
            beta[i] = (float)(predictedBeta + k1 * innovation);

            p00 = pp00 - k0 * h1 * pp10;
            p01 = pp01 - k0 * h1 * pp11;
            p10 = pp10 - k1 * h1 * pp10;
            p11 = pp11 - k1 * h1 * pp11;

            x[i] = x[i - 1] + 0.5f * (MathF.Cos(beta[i - 1]) + MathF.Cos(beta[i])) * ds;
        }

        float yFinal = y[^1];
        int denominator = Math.Max(1, y.Length - 1);
        for (int i = 0; i < y.Length; i++)
        {
            y[i] -= yFinal * i / denominator;
        }
    }

    private static void IntegrateShapeCircularArc(
        float[] arcPositions,
        float[] curvature,
        float initialAngleRad,
        out float[] x,
        out float[] y)
    {
        x = new float[arcPositions.Length];
        y = new float[arcPositions.Length];
        if (arcPositions.Length == 0)
        {
            return;
        }

        double heading = initialAngleRad;
        for (int i = 0; i < arcPositions.Length - 1; i++)
        {
            double ds = Math.Max(0.000001d, arcPositions[i + 1] - arcPositions[i]);
            double k0 = float.IsFinite(curvature[i]) ? curvature[i] : 0d;
            double k1 = float.IsFinite(curvature[i + 1]) ? curvature[i + 1] : k0;
            double k = 0.5d * (k0 + k1);
            double dTheta = k * ds;

            double chord;
            if (Math.Abs(k) < 1e-9d || Math.Abs(dTheta) < 1e-9d)
            {
                chord = ds;
            }
            else
            {
                chord = 2d * Math.Sin(dTheta / 2d) / k;
            }

            double midHeading = heading + dTheta / 2d;
            x[i + 1] = x[i] + (float)(chord * Math.Cos(midHeading));
            y[i + 1] = y[i] + (float)(chord * Math.Sin(midHeading));
            heading += dTheta;
        }
    }
}
