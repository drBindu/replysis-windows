using System;

namespace InterviewCopilot
{
    /// <summary>
    /// How listening time becomes billed minutes.
    ///
    /// Pulled out of MainWindow unchanged, so the arithmetic can be read and
    /// tested rather than inferred from two call sites that round in opposite
    /// directions. Nothing here changes what a user is charged; it makes what
    /// they are charged visible.
    ///
    /// It needed to be visible because the two roundings compound. The meter
    /// tick FLOORS whole minutes and carries the remainder forward; the stop
    /// path then ROUNDS THAT REMAINDER UP to a whole minute whenever it is 30
    /// seconds or more. Every turn therefore pays for its final partial minute
    /// as a full one, on top of the whole minutes already reported.
    ///
    /// A turn is not a session. Both Manual and Auto call StopListeningMeter at
    /// the end of every turn, so in a normal interview the round-up happens
    /// once per exchange rather than once per sitting:
    ///
    ///     10 turns of 40s  =  6m 40s of listening  ->  10 minutes billed
    ///
    /// The 30-second floor is deliberate and the comment explaining it is
    /// right on its own terms - rounding every short turn down to nothing would
    /// make an interview of brief exchanges free. What was not decided is what
    /// happens when that floor is applied per turn to a remainder that has
    /// already had its whole minutes taken out.
    ///
    /// Not changed here. What a customer is charged is the owner's decision,
    /// not a thing to alter quietly while tidying.
    /// </summary>
    internal static class ListeningBilling
    {
        /// <summary>Minutes reported by the periodic tick: whole minutes only,
        /// remainder carried forward in <paramref name="carrySeconds"/>.</summary>
        internal static int MinutesOnTick(double unreportedSeconds, out double carrySeconds)
        {
            int minutes = (int)(unreportedSeconds / 60);
            carrySeconds = unreportedSeconds - minutes * 60.0;
            return minutes;
        }

        /// <summary>How long a remainder must be to be billed at all.</summary>
        internal const double ShortTurnFloorSeconds = 30;

        /// <summary>Minutes reported when a turn ends. Anything at or above the
        /// floor becomes at least one minute.</summary>
        internal static int MinutesOnStop(double unreportedSeconds)
        {
            if (unreportedSeconds < ShortTurnFloorSeconds) return 0;
            return Math.Max(1, (int)Math.Round(unreportedSeconds / 60.0));
        }

        /// <summary>
        /// What a whole sitting costs, given the length of each turn in it.
        /// Exists so the compounding is a number rather than an argument.
        /// </summary>
        internal static int MinutesForSession(params double[] turnSeconds)
        {
            int total = 0;
            foreach (double turn in turnSeconds)
            {
                double unreported = turn;
                // The tick fires every minute of an ongoing turn.
                total += MinutesOnTick(unreported, out double carry);
                total += MinutesOnStop(carry);
            }
            return total;
        }
    }
}
