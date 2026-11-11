using k8s.Models;
using KubePortal.Core;

namespace KubePortal.Tests.Core;

public class ServicePortTranslationTests
{
    private static V1Service Service(int port, IntstrIntOrString? targetPort) => new()
    {
        Spec = new V1ServiceSpec
        {
            Ports = new List<V1ServicePort> { new() { Port = port, TargetPort = targetPort } }
        }
    };

    private static V1Pod Pod(params (string Name, int Port)[] ports) => new()
    {
        Metadata = new V1ObjectMeta { Name = "pod-1" },
        Spec = new V1PodSpec
        {
            Containers = new List<V1Container>
            {
                new()
                {
                    Name = "app",
                    Ports = ports.Select(p => new V1ContainerPort(p.Port, name: p.Name)).ToList()
                }
            }
        }
    };

    [Fact]
    public void NamedTargetPort_ResolvesToContainerPort()
    {
        // The grafana case: svc port 80 -> targetPort "grafana" -> containerPort 3000.
        var svc = Service(80, new IntstrIntOrString("grafana"));
        var pod = Pod(("grafana", 3000), ("profiling", 6060));

        Assert.Equal(3000, KubernetesCache.TranslateServicePort(svc, 80, pod, "grafana"));
    }

    [Fact]
    public void NumericTargetPort_IsUsedInsteadOfServicePort()
    {
        // The pcms db03 case: svc port 1433 -> targetPort 1434.
        var svc = Service(1433, new IntstrIntOrString("1434"));

        Assert.Equal(1434, KubernetesCache.TranslateServicePort(svc, 1433, Pod(), "db-tunnel"));
    }

    [Fact]
    public void MatchingTargetPort_PassesThrough()
    {
        var svc = Service(5001, new IntstrIntOrString("5001"));

        Assert.Equal(5001, KubernetesCache.TranslateServicePort(svc, 5001, Pod(), "dems-api"));
    }

    [Fact]
    public void MissingTargetPort_DefaultsToServicePort()
    {
        var svc = Service(8080, null);

        Assert.Equal(8080, KubernetesCache.TranslateServicePort(svc, 8080, Pod(), "svc"));
    }

    [Fact]
    public void UnresolvableNamedPort_ReturnsNullRatherThanForwardingBlind()
    {
        var svc = Service(80, new IntstrIntOrString("http-web"));
        var pod = Pod(("grafana", 3000));

        Assert.Null(KubernetesCache.TranslateServicePort(svc, 80, pod, "grafana"));
    }

    [Fact]
    public void UnknownServicePort_FallsBackToTheRequestedPort()
    {
        var svc = Service(80, new IntstrIntOrString("grafana"));

        Assert.Equal(9999, KubernetesCache.TranslateServicePort(svc, 9999, Pod(), "grafana"));
    }
}
