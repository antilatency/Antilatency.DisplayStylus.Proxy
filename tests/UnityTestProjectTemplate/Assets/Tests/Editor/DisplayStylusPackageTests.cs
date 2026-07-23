using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Antilatency.DisplayStylus.SDK.Tests {
    public sealed class DisplayStylusPackageTests {
        [Test]
        public void RuntimeAssemblyAndConnectionModesAreAvailable() {
            Assert.That(
                typeof(DisplayStylusConnection).Assembly.GetName().Name,
                Is.EqualTo("Antilatency.DisplayStylus.SDK"));
            Assert.That(DisplayStylusConnectionMode.LocalAdn, Is.Not.EqualTo(DisplayStylusConnectionMode.Proxy));
            Assert.That(typeof(DisplayStylusProxyWriter).GetMethod(nameof(DisplayStylusProxyWriter.AcquireAsync)), Is.Not.Null);
            Assert.That(typeof(DisplayStylusProxyWriter).GetMethod(nameof(DisplayStylusProxyWriter.SetDisplayConfigAsync)), Is.Not.Null);
        }

        [Test]
        public void ConnectionRunsBeforeUpstreamDeviceNetworkAwake() {
            var attribute = (DefaultExecutionOrder)Attribute.GetCustomAttribute(
                typeof(DisplayStylusConnection),
                typeof(DefaultExecutionOrder));

            Assert.That(attribute, Is.Not.Null);
            Assert.That(attribute.order, Is.LessThan(0));
        }

        [Test]
        public void CreateInSceneDoesNotCreateAnEagerLocalDeviceNetwork() {
            EditorMenu.CreateInScene();
            var handle = Selection.activeGameObject;
            try {
                Assert.That(handle, Is.Not.Null);
                Assert.That(handle.transform.childCount, Is.EqualTo(1));
                var display = handle.transform.GetChild(0).gameObject;
                Assert.That(display.GetComponent<DisplayStylusConnection>(), Is.Not.Null);
                Assert.That(display.GetComponent<Antilatency.SDK.DeviceNetwork>(), Is.Null);
            }
            finally {
                if (handle != null) {
                    UnityEngine.Object.DestroyImmediate(handle);
                }
            }
        }

        [UnityTest]
        [Explicit("Requires the production proxy and a physical display on 127.0.0.1:48192.")]
        public IEnumerator ProxyModeReceivesPhysicalDisplayWithoutCreatingLocalAdn() {
            var host = new GameObject("Proxy integration test");
            host.SetActive(false);
            var connection = host.AddComponent<DisplayStylusConnection>();
            connection.Mode = DisplayStylusConnectionMode.Proxy;
            host.SetActive(true);

            var update = typeof(DisplayStylusConnection).GetMethod(
                "Update",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(update, Is.Not.Null);

            try {
                var deadline = EditorApplication.timeSinceStartup + 5.0;
                while (!connection.IsReady && EditorApplication.timeSinceStartup < deadline) {
                    update.Invoke(connection, null);
                    yield return null;
                }

                Assert.That(connection.ConnectionStatus, Does.Contain("display ready"));
                Assert.That(connection.IsReady, Is.True, connection.ConnectionStatus);
                Assert.That(host.GetComponent<Antilatency.SDK.DeviceNetwork>(), Is.Null);
            }
            finally {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }
    }
}
