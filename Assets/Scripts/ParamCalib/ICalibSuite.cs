public interface ICalibSuite {
    public bool TryGetParameter<T>(string id, out T parameter) where T : ICalibParameter;
    public void RegisterParameter<T>(string id, T parameter) where T : ICalibParameter;

    public string ToJson();
}