// Compile-time surface used only to validate the Unity package in CI without Unity Editor.
// Runtime behavior is intentionally not implemented here.
using System;
using System.Text.Json;

namespace UnityEngine {
    public class Object {
        public static void Destroy(Object value) { }
        public static GameObject Instantiate(GameObject original, Vector3 position, Quaternion rotation, Transform parent) => new();
    }

    public class Component : Object {
        public GameObject gameObject { get; } = new();
        public Transform transform { get; } = new();
        public T GetComponent<T>() where T : class => default;
        public T GetComponentInParent<T>() where T : class => default;
    }

    public class Behaviour : Component {
        public bool isActiveAndEnabled { get; set; }
        public bool enabled { get; set; }
    }

    public class MonoBehaviour : Behaviour { }

    public class GameObject : Object {
        public GameObject() { }
        public GameObject(string name) { }
        public Transform transform { get; } = new();
        public T GetComponent<T>() where T : class => default;
        public T AddComponent<T>() where T : Component, new() => new();
    }

    public class Transform : Component {
        public Transform parent { get; set; }
        public Vector3 localPosition { get; set; }
        public Quaternion localRotation { get; set; }
        public Vector3 position { get; set; }
        public Quaternion rotation { get; set; }
        public Vector3 TransformVector(Vector3 value) => value;
        public Vector3 TransformDirection(Vector3 value) => value;
        public void SetParent(Transform value) => parent = value;
    }

    public struct Vector2 {
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public float x;
        public float y;
    }

    public struct Vector3 {
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public float x;
        public float y;
        public float z;
        public float magnitude => 0;
        public Vector3 normalized => this;
        public static Vector3 zero => default;
        public static Vector3 Cross(Vector3 left, Vector3 right) => default;
        public static Vector3 operator *(Vector3 value, float scalar) => value;
        public static Vector3 operator *(float scalar, Vector3 value) => value;
        public static Vector3 operator -(Vector3 value) => value;
    }

    public struct Vector4 {
        public float x;
        public float y;
        public float z;
        public float w;
        public static implicit operator Vector4(Vector3 value) => new() { x = value.x, y = value.y, z = value.z };
    }

    public struct Quaternion {
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public float x;
        public float y;
        public float z;
        public float w;
        public static Quaternion identity => new(0, 0, 0, 1);
        public static Quaternion Inverse(Quaternion value) => value;
        public static Vector3 operator *(Quaternion rotation, Vector3 point) => point;
        public static Quaternion operator *(Quaternion left, Quaternion right) => left;
    }

    public struct Pose {
        public Pose(Vector3 position, Quaternion rotation) { this.position = position; this.rotation = rotation; }
        public Vector3 position;
        public Quaternion rotation;
        public static Pose identity => new(Vector3.zero, Quaternion.identity);
    }

    public struct Matrix4x4 {
        public Matrix4x4(Vector4 column0, Vector4 column1, Vector4 column2, Vector4 column3) { }
    }

    public static class Mathf {
        public static float Max(float left, float right) => System.Math.Max(left, right);
    }

    public static class Time {
        public static double realtimeSinceStartupAsDouble => 0;
    }

    public static class Application {
        public static bool isEditor => false;
        public static bool isDebugBuild => false;
        public static bool isPlaying => false;
        public static event Action onBeforeRender;
    }

    public static class Debug {
        public static void LogError(object message) { }
        public static void LogError(object message, Object context) { }
        public static void LogWarning(object message, Object context) { }
        public static void LogException(Exception exception, Object context) { }
    }

    public static class JsonUtility {
        private static readonly JsonSerializerOptions Options = new() { IncludeFields = true };
        public static string ToJson(object value) => JsonSerializer.Serialize(value, value.GetType(), Options);
        public static T FromJson<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class RequireComponent : Attribute {
        public RequireComponent(Type componentType) { }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class DisallowMultipleComponent : Attribute { }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class DefaultExecutionOrder : Attribute {
        public DefaultExecutionOrder(int order) { }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeField : Attribute { }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class MinAttribute : Attribute {
        public MinAttribute(float minimum) { }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class HeaderAttribute : Attribute {
        public HeaderAttribute(string header) { }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class BeforeRenderOrderAttribute : Attribute {
        public BeforeRenderOrderAttribute(int order) { }
    }
}

namespace UnityEditor {
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class MenuItem : Attribute {
        public MenuItem(string path) { }
    }

    public static class Undo {
        public static void RegisterCreatedObjectUndo(UnityEngine.Object value, string name) { }
        public static void DestroyObjectImmediate(UnityEngine.Object value) { }
    }

    public static class EditorApplication {
        public static Action delayCall { get; set; }
    }

    public static class EditorUtility {
        public static void SetDirty(UnityEngine.Object value) { }
    }

    public static class Selection {
        public static UnityEngine.GameObject activeGameObject { get; set; }
    }
}

namespace Antilatency.SDK {
    public sealed class DeviceNetwork : UnityEngine.MonoBehaviour {
        public Antilatency.DeviceNetwork.INetwork NativeNetwork { get; set; }
    }
}

namespace Antilatency.DisplayStylus.SDK {
    public sealed class DisplayHandle : UnityEngine.MonoBehaviour { }
}
