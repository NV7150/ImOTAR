public interface ICalibSuite {
    public bool TryGetParameter<T>(out T parameter) where T : ICalibParameter;
    public void RegisterParameter<T>(T parameter) where T : ICalibParameter;
}