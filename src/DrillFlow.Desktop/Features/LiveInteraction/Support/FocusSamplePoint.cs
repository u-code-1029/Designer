namespace DrillFlow.Desktop.ViewModels;

public sealed class FocusSamplePoint
{
    public FocusSamplePoint(double zMetres, double sharpness)
    {
        ZMetres = zMetres;
        Sharpness = sharpness;
    }

    public double ZMetres { get; }

    public double Sharpness { get; }
}
