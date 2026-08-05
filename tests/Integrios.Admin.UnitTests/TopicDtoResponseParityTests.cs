using System.Reflection;
using Integrios.Admin.Endpoints;
using Integrios.Application.Topics;

namespace Integrios.Admin.UnitTests;

public sealed class TopicDtoResponseParityTests
{
    [Fact]
    public void AdminTopicResponse_HasACounterpartForEveryTopicDtoProperty()
    {
        AssertParity(typeof(TopicDto), typeof(AdminTopicResponse));
    }

    [Fact]
    public void AdminTopicListResponse_HasACounterpartForEveryTopicListDtoProperty()
    {
        AssertParity(typeof(TopicListDto), typeof(AdminTopicListResponse));
    }

    private static void AssertParity(Type dtoType, Type responseType)
    {
        Dictionary<string, PropertyInfo> responseProperties = responseType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(p => p.Name, StringComparer.Ordinal);

        foreach (PropertyInfo dtoProperty in dtoType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.True(
                responseProperties.TryGetValue(dtoProperty.Name, out PropertyInfo? responseProperty),
                $"{responseType.Name} has no counterpart for {dtoType.Name}.{dtoProperty.Name}.");

            (Type dtoElementType, bool dtoIsList) = UnwrapList(dtoProperty.PropertyType);
            if (dtoIsList)
            {
                (Type responseElementType, bool responseIsList) = UnwrapList(responseProperty!.PropertyType);
                Assert.True(
                    responseIsList,
                    $"{responseType.Name}.{responseProperty.Name} must be a list to match {dtoType.Name}.{dtoProperty.Name}.");
                if (IsApplicationModelType(dtoElementType))
                    AssertParity(dtoElementType, responseElementType);
                continue;
            }

            if (IsApplicationModelType(dtoProperty.PropertyType))
                AssertParity(dtoProperty.PropertyType, responseProperty!.PropertyType);
        }
    }

    private static bool IsApplicationModelType(Type type) =>
        type.Namespace?.StartsWith("Integrios.Application", StringComparison.Ordinal) == true;

    private static (Type ElementType, bool IsList) UnwrapList(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
            ? (type.GetGenericArguments()[0], true)
            : (type, false);
}
