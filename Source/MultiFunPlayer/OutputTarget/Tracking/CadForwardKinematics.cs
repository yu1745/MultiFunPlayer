namespace MultiFunPlayer.OutputTarget.Tracking;

// Closure solve of the exact model in firmware include/cad_8108_geometry.hpp,
// SHA e15e328f624c0c1bccbd2751bb7fc3017519342d71a9068e6a5bd066072eca2f.
// Six measured CAD joint angles -> five receiver DOFs. R0 is independent.
internal static class CadForwardKinematics
{
    internal readonly record struct V(double X, double Y, double Z)
    {
        public static V operator +(V a, V b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V operator -(V a, V b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V operator *(V a, double b) => new(a.X * b, a.Y * b, a.Z * b);
        public double Dot(V b) => X * b.X + Y * b.Y + Z * b.Z;
        public double Length => Math.Sqrt(Dot(this));
        public V Cross(V b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
    }
    private sealed record Link(V Pivot, V Lower, V Upper);
    internal static readonly V Pivot = new(100.062295, 270.186359, -62.585786);
    private static readonly Link[] Links = [
        new(new(50,51.50003706667658,-151.99999999996183),new(-54.277055341214464,63.80026996549992,-151.99999999996183),new(100.062295,270.186359,-164.171573)),
        new(new(145,51.50003706667658,-151.99999999996183),new(249.45712500886327,62.1634809232698,-151.99999999996183),new(100.062295,270.186359,-183.171573)),
        new(new(240,51.50003706667658,-66.99999999994655),new(338.5122304849094,87.8365262323635,-66.99999999994655),new(183.062295,270.186359,-164.171573)),
        new(new(240,51.50003706667658,-41.00000000005345),new(340.6423539601903,81.43524358595371,-41.00000000005345),new(183.062295,270.186359,39)),
        new(new(145,51.50003706667658,43.99999999997688),new(249.62013035745935,60.423507467154835,43.99999999997688),new(100.062295,270.186359,58)),
        new(new(50,51.50003706667658,43.99999999997688),new(-54.30903836838205,63.526030358850065,43.99999999997688),new(100.062295,270.186359,39))
    ];
    internal sealed record Result(bool Valid, double[] Receiver, double MaxResidualMm, int Iterations);
    private static V Rotate(V v, double roll, double pitch)
    {
        var x = Math.Cos(pitch) * v.X - Math.Sin(pitch) * v.Y;
        var y = Math.Sin(pitch) * v.X + Math.Cos(pitch) * v.Y;
        return new(Math.Cos(roll) * x + Math.Sin(roll) * v.Z, y, -Math.Sin(roll) * x + Math.Cos(roll) * v.Z);
    }

    // Receiver [x mm,y mm,z mm,roll rad,pitch rad], all in CAD conventions.
    public static Result Solve(double[] joints, double[] seed = null, double toleranceMm = .5)
    {
        if (joints.Length < 6 || joints.Take(6).Any(x => !double.IsFinite(x))) return new(false, [], double.PositiveInfinity, 0);
        var pose = seed?.Length == 5 && seed.All(double.IsFinite) ? (double[])seed.Clone() : new double[5];
        var lower = Links.Select((l, i) => {
            var a = l.Lower - l.Pivot; var c = Math.Cos(joints[i]); var s = Math.Sin(joints[i]);
            return l.Pivot + new V(c * a.X - s * a.Y, s * a.X + c * a.Y, a.Z);
        }).ToArray();
        var damping = 1e-5;
        var last = Evaluate(pose, lower, false);
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var evaluated = Evaluate(pose, lower, true);
            var residual = evaluated.Residual;
            var normal = new double[5, 5]; var rhs = new double[5];
            for (var i = 0; i < 5; i++)
                for (var row = 0; row < 6; row++)
                {
                    rhs[i] -= evaluated.Jacobian[row, i] * residual[row];
                    for (var j = 0; j < 5; j++) normal[i, j] += evaluated.Jacobian[row, i] * evaluated.Jacobian[row, j];
                }
            for (var i = 0; i < 5; i++) normal[i, i] += damping;
            if (!LinearSolve(normal, rhs, out var step)) return new(false, pose, double.PositiveInfinity, iteration);
            var trial = (double[])pose.Clone();
            // Scaled angular coordinates improve conditioning and bound branch jumps.
            for (var i = 0; i < 5; i++) trial[i] += Math.Clamp(step[i], -20, 20) / (i < 3 ? 1 : 100);
            if (Math.Abs(trial[3]) > 1.2 || Math.Abs(trial[4]) > 1.2 || trial.Take(3).Any(x => Math.Abs(x) > 300))
                return new(false, pose, evaluated.Max, iteration + 1);
            last = Evaluate(trial, lower, false);
            if (last.Cost <= evaluated.Cost + 1e-15)
            {
                pose = trial; damping = Math.Max(1e-10, damping * .2);
                if (step.Max(Math.Abs) < 1e-6 || last.Max < 1e-6)
                    return new(last.Max <= toleranceMm, pose, last.Max, iteration + 1);
            }
            else damping = Math.Min(1e8, damping * 10);
        }
        last = Evaluate(pose, lower, false);
        return new(last.Max <= toleranceMm, pose, last.Max, 32);
    }

    private static (double Cost, double Max, double[] Residual, double[,] Jacobian) Evaluate(double[] pose, V[] lower, bool derivative)
    {
        var residual = new double[6]; var jacobian = new double[6, 5];
        var cost = 0d; var max = 0d;
        for (var i = 0; i < 6; i++)
        {
            var rotated = Rotate(Links[i].Upper - Pivot, pose[3], pose[4]);
            var upper = Pivot + new V(pose[1], -pose[0], -pose[2]) + rotated;
            var rod = upper - lower[i]; var length = rod.Length;
            var r = length - (Links[i].Upper - Links[i].Lower).Length;
            residual[i] = r; cost += r * r; max = Math.Max(max, Math.Abs(r));
            if (!derivative || length < 1e-9) continue;
            var rollDerivative = new V(0, 1, 0).Cross(rotated);
            var pitchDerivative = new V(Math.Sin(pose[3]), 0, Math.Cos(pose[3])).Cross(rotated);
            jacobian[i, 0] = -rod.Y / length; jacobian[i, 1] = rod.X / length; jacobian[i, 2] = -rod.Z / length;
            jacobian[i, 3] = rod.Dot(rollDerivative) / (length * 100);
            jacobian[i, 4] = rod.Dot(pitchDerivative) / (length * 100);
        }
        return (cost, max, residual, jacobian);
    }

    private static bool LinearSolve(double[,] matrix, double[] rhs, out double[] result)
    {
        var a = new double[5, 6]; result = new double[5];
        for (var i = 0; i < 5; i++) { for (var j = 0; j < 5; j++) a[i, j] = matrix[i, j]; a[i, 5] = rhs[i]; }
        for (var c = 0; c < 5; c++)
        {
            var pivot = c;
            for (var i = c + 1; i < 5; i++) if (Math.Abs(a[i, c]) > Math.Abs(a[pivot, c])) pivot = i;
            if (!double.IsFinite(a[pivot, c]) || Math.Abs(a[pivot, c]) < 1e-15) return false;
            for (var j = c; j <= 5; j++) (a[c, j], a[pivot, j]) = (a[pivot, j], a[c, j]);
            var d = a[c, c]; for (var j = c; j <= 5; j++) a[c, j] /= d;
            for (var i = 0; i < 5; i++) if (i != c) { var f = a[i, c]; for (var j = c; j <= 5; j++) a[i, j] -= f * a[c, j]; }
        }
        for (var i = 0; i < 5; i++) result[i] = a[i, 5];
        return result.All(double.IsFinite);
    }

    public static double[] TipAxes(double[] receiver, double twistRadians, double stickMm)
    {
        var r = receiver[3]; var p = receiver[4];
        return [receiver[1] + stickMm * (1 - Math.Cos(p) * Math.Cos(r)),
            receiver[0] + stickMm * Math.Sin(p), receiver[2] - stickMm * Math.Cos(p) * Math.Sin(r),
            twistRadians * 180 / Math.PI, -r * 180 / Math.PI, -p * 180 / Math.PI];
    }

    public static double[] SeedFromTip(double[] target, double stickMm)
    {
        var r = -target[4] * Math.PI / 180; var p = -target[5] * Math.PI / 180;
        return [target[1] - stickMm * Math.Sin(p), target[0] - stickMm * (1 - Math.Cos(p) * Math.Cos(r)),
            target[2] + stickMm * Math.Cos(p) * Math.Sin(r), r, p];
    }
}
