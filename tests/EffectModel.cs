namespace G29.Tests
{
    // The effect parameters of the frozen engine and DIEFFECT vectors, as plain
    // test data (formerly the legacy C# engine's model).
    // Values follow DirectInput units: magnitudes and gains 0..10000, times in
    // microseconds, phase in hundredths of a degree.
    public enum EffectKind
    {
        Constant = 1,
        Ramp = 2,
        Square = 3,
        Sine = 4,
        Triangle = 5,
        SawtoothUp = 6,
        SawtoothDown = 7,
        Spring = 8,
        Damper = 9,
        Inertia = 10,
        Friction = 11
    }

    public sealed class EffectEnvelope
    {
        public uint AttackLevel { get; set; }

        public uint AttackTime { get; set; }

        public uint FadeLevel { get; set; }

        public uint FadeTime { get; set; }
    }

    public sealed class EffectCondition
    {
        public int Offset { get; set; }

        public int PositiveCoefficient { get; set; }

        public int NegativeCoefficient { get; set; }

        public uint PositiveSaturation { get; set; }

        public uint NegativeSaturation { get; set; }

        public int DeadBand { get; set; }
    }

    public sealed class EffectParameters
    {
        public const uint Infinite = 0xFFFFFFFF;

        public EffectParameters(EffectKind kind)
        {
            Kind = kind;
            Duration = Infinite;
            Gain = 10000;
            DirectionSign = 1;
            Condition = new EffectCondition();
        }

        public EffectKind Kind { get; private set; }

        public uint Duration { get; set; }

        public uint Gain { get; set; }

        public uint StartDelay { get; set; }

        // +1 pushes toward the positive end of the steering axis, -1 toward the negative end.
        public int DirectionSign { get; set; }

        public EffectEnvelope Envelope { get; set; }

        // Constant: signed magnitude. Periodic: unsigned magnitude.
        public int Magnitude { get; set; }

        public int RampStart { get; set; }

        public int RampEnd { get; set; }

        // Periodic offset.
        public int Offset { get; set; }

        public uint Phase { get; set; }

        public uint Period { get; set; }

        public EffectCondition Condition { get; set; }

        public bool IsCondition
        {
            get { return Kind >= EffectKind.Spring; }
        }

        public bool IsPeriodic
        {
            get { return Kind >= EffectKind.Square && Kind <= EffectKind.SawtoothDown; }
        }

        public EffectParameters Clone()
        {
            var clone = (EffectParameters)MemberwiseClone();
            if (Envelope != null)
            {
                clone.Envelope = new EffectEnvelope
                {
                    AttackLevel = Envelope.AttackLevel,
                    AttackTime = Envelope.AttackTime,
                    FadeLevel = Envelope.FadeLevel,
                    FadeTime = Envelope.FadeTime
                };
            }

            clone.Condition = new EffectCondition
            {
                Offset = Condition.Offset,
                PositiveCoefficient = Condition.PositiveCoefficient,
                NegativeCoefficient = Condition.NegativeCoefficient,
                PositiveSaturation = Condition.PositiveSaturation,
                NegativeSaturation = Condition.NegativeSaturation,
                DeadBand = Condition.DeadBand
            };
            return clone;
        }
    }
}
