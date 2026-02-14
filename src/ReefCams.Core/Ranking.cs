namespace ReefCams.Core;

public enum RankBucket
{
    Zero = 0,
    VeryLow = 1,
    Low = 2,
    Medium = 3,
    High = 4,
    VeryHigh = 5
}

public static class Ranker
{
    public static RankBucket Classify(double maxConf, RankThresholds thresholds)
    {
        if (maxConf > thresholds.VeryHighExclusiveLower)
        {
            return RankBucket.VeryHigh;
        }

        if (maxConf >= thresholds.HighInclusiveLower)
        {
            return RankBucket.High;
        }

        if (maxConf >= thresholds.MediumInclusiveLower)
        {
            return RankBucket.Medium;
        }

        if (maxConf >= thresholds.LowInclusiveLower)
        {
            return RankBucket.Low;
        }

        if (maxConf >= thresholds.VeryLowInclusiveLower)
        {
            return RankBucket.VeryLow;
        }

        return RankBucket.Zero;
    }

    public static double LowerBound(RankBucket bucket, RankThresholds thresholds) =>
        bucket switch
        {
            RankBucket.VeryHigh => thresholds.VeryHighExclusiveLower,
            RankBucket.High => thresholds.HighInclusiveLower,
            RankBucket.Medium => thresholds.MediumInclusiveLower,
            RankBucket.Low => thresholds.LowInclusiveLower,
            RankBucket.VeryLow => thresholds.VeryLowInclusiveLower,
            _ => 0.0
        };

    public static string DisplayName(RankBucket bucket) =>
        bucket switch
        {
            RankBucket.VeryHigh => "Very High",
            RankBucket.High => "High",
            RankBucket.Medium => "Medium",
            RankBucket.Low => "Low",
            RankBucket.VeryLow => "Very Low",
            _ => "Zero"
        };
}
