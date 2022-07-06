using Grpc.Core;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using XamlTest.Internal;

#if WPF
using Window = System.Windows.Window;
#endif

namespace XamlTest.Host;

#if WPF
internal partial class VisualTreeService : Protocol.ProtocolBase
{
    private Dictionary<string, WeakReference<DependencyObject>> KnownElements { get; } = new();

    public override async Task<ElementResult> GetElement(ElementQuery request, ServerCallContext context)
    {
        ElementResult reply = new();
        await Application.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                FrameworkElement? searchRoot = GetParentElement();

                if (searchRoot is null) return;

                var window = searchRoot as Window ?? Window.GetWindow(searchRoot);
                window.LogMessage("Getting element");

                if (!string.IsNullOrWhiteSpace(request.Query))
                {
                    if (!(EvaluateQuery(searchRoot, request.Query) is DependencyObject element))
                    {
                        reply.ErrorMessages.Add($"Failed to find element by query '{request.Query}' in '{searchRoot.GetType().FullName}'");
                        return;
                    }

                    reply.Elements.Add(GetElement(element));

                    window.LogMessage("Got element");
                    return;
                }

                reply.ErrorMessages.Add($"{nameof(ElementQuery)} did not specify a query");
            }
            catch (Exception e)
            {
                reply.ErrorMessages.Add(e.ToString());
            }
        });
        return reply;

        FrameworkElement? GetParentElement()
        {
            if (!string.IsNullOrWhiteSpace(request.WindowId))
            {
                Window? window = GetCachedElement<Window>(request.WindowId);
                if (window is null)
                {
                    reply!.ErrorMessages.Add("Failed to find parent window");
                }
                return window;
            }
            if (!string.IsNullOrWhiteSpace(request.ParentId))
            {
                FrameworkElement? element = GetCachedElement<FrameworkElement>(request.ParentId);
                if (element is null)
                {
                    reply!.ErrorMessages.Add("Failed to find parent element");
                }
                return element;
            }
            reply!.ErrorMessages.Add("No parent element specified as part of the query");
            return null;
        }
    }

    private static object? EvaluateQuery(DependencyObject root, string query)
    {
        object? result = null;
        List<string> errorParts = new();
        DependencyObject? current = root;

        while (query.Length > 0)
        {
            if (current is null)
            {
                throw new XAMLTestException($"Could not resolve '{query}' on null element");
            }

            switch (GetNextQueryType(ref query, out string value))
            {
                case QueryPartType.Name:
                    result = EvaluateNameQuery(current, value);
                    break;
                case QueryPartType.Property:
                    result = EvaluatePropertyQuery(current, value);
                    break;
                case QueryPartType.ChildType:
                    result = EvaluateChildTypeQuery(current, value);
                    break;
                case QueryPartType.PropertyExpression:
                    result = EvaluatePropertyExpressionQuery(current, value);
                    break;
            }
            current = result as DependencyObject;
        }

        return result;

        static QueryPartType GetNextQueryType(ref string query, out string value)
        {
            Regex propertyExpressionRegex = new(@"(?<=^\[[^=\]]+=[^=\]]+)\]");
            Regex regex = new(@"(?<=.)[\.\/\~]");

            string currentQuery = query;
            if (propertyExpressionRegex.Match(query) is { } propertyExpressionMatch &&
                propertyExpressionMatch.Success)
            {
                currentQuery = query.Substring(0, propertyExpressionMatch.Index + 1);
                query = query[(propertyExpressionMatch.Index + 1)..];
            }
            else if (regex.Match(query) is { } match &&
                match.Success)
            {
                currentQuery = query.Substring(0, match.Index);
                query = query[match.Index..];
            }
            else
            {
                query = "";
            }

            QueryPartType rv;
            if (currentQuery.StartsWith('[') && currentQuery.EndsWith(']'))
            {
                value = currentQuery[1..^1];
                rv = QueryPartType.PropertyExpression;
            }
            else if (currentQuery.StartsWith('.'))
            {
                value = currentQuery[1..];
                rv = QueryPartType.Property;
            }
            else if (currentQuery.StartsWith('/'))
            {
                value = currentQuery[1..];
                rv = QueryPartType.ChildType;
            }
            else
            {
                if (currentQuery.StartsWith('~'))
                {
                    value = currentQuery[1..];
                }
                else
                {
                    value = currentQuery;
                }
                rv = QueryPartType.Name;
            }
            return rv;
        }

        static object? EvaluateNameQuery(DependencyObject root, string name)
        {
            return Decendants<FrameworkElement>(root).FirstOrDefault(x => x.Name == name);
        }

        static object? EvaluatePropertyQuery(DependencyObject root, string property)
        {
            var properties = TypeDescriptor.GetProperties(root);
            if (properties.Find(property, false) is PropertyDescriptor propertyDescriptor)
            {
                return propertyDescriptor.GetValue(root);
            }
            throw new XAMLTestException($"Failed to find property '{property}' on element of type '{root.GetType().FullName}'");
        }

        static object EvaluateChildTypeQuery(DependencyObject root, string childTypeQuery)
        {
            Regex indexerRegex = new(@"\[(?<Index>\d+)]$");

            int index = 0;
            Match match = indexerRegex.Match(childTypeQuery);
            if (match.Success)
            {
                index = int.Parse(match.Groups["Index"].Value);
                childTypeQuery = childTypeQuery.Substring(0, match.Index);
            }

            foreach (DependencyObject child in Decendants<DependencyObject>(root))
            {
                if (GetTypeNames(child).Any(x => x == childTypeQuery))
                {
                    if (index == 0)
                    {
                        return child;
                    }
                    index--;
                }
            }
            throw new XAMLTestException($"Failed to find child element of type '{childTypeQuery}'");
        }

        static object EvaluatePropertyExpressionQuery(DependencyObject root, string propertyExpression)
        {
            var parts = propertyExpression.Split('=');
            string property = parts[0].TrimEnd();
            string propertyValueString = parts[1].Trim('"');

            foreach (DependencyObject child in Decendants<DependencyObject>(root))
            {
                var properties = TypeDescriptor.GetProperties(child);
                if (properties.Find(property, false) is PropertyDescriptor propertyDescriptor)
                {
                    var value = propertyDescriptor.GetValue(child)?.ToString();
                    //TODO: More advanced comparison
                    if (string.Equals(value, propertyValueString))
                    {
                        return child;
                    }
                }
            }
            throw new XAMLTestException($"Failed to find child element with property expression '{propertyExpression}'");
        }

        static IEnumerable<string> GetTypeNames(DependencyObject child)
        {
            for (Type? type = child.GetType();
                type is not null;
                type = type.BaseType)
            {
                yield return type.Name;
            }
        }
    }

    private enum QueryPartType
    {
        None,
        Name,
        Property,
        ChildType,
        PropertyExpression
    }

    private static IEnumerable<T> Decendants<T>(DependencyObject? parent)
        where T : DependencyObject
    {
        if (parent is null) yield break;

        var queue = new Queue<DependencyObject>();
        Enqueue(GetChildren(parent));

        if (parent is UIElement parentVisual &&
            AdornerLayer.GetAdornerLayer(parentVisual) is { } layer &&
            layer.GetAdorners(parentVisual) is { } adorners &&
            adorners.Length > 0)
        {
            Enqueue(adorners);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current is T match) yield return match;

            Enqueue(GetChildren(current));
        }

        static IEnumerable<DependencyObject> GetChildren(DependencyObject item)
        {
            int childrenCount = VisualTreeHelper.GetChildrenCount(item);
            for (int i = 0; i < childrenCount; i++)
            {
                if (VisualTreeHelper.GetChild(item, i) is DependencyObject child)
                {
                    yield return child;
                }
            }
            if (item is FrameworkElement fe)
            {
                if (fe.ContextMenu is { } contextMenu)
                {
                    yield return contextMenu;
                }
                if (fe.ToolTip as DependencyObject is { } toolTip)
                {
                    yield return toolTip;
                }
            }
            if (childrenCount == 0)
            {
                foreach (object? logicalChild in LogicalTreeHelper.GetChildren(item))
                {
                    if (logicalChild is DependencyObject child)
                    {
                        yield return child;
                    }
                }
            }
        }

        void Enqueue(IEnumerable<DependencyObject> items)
        {
            foreach (var item in items)
            {
                queue!.Enqueue(item);
            }
        }
    }

    private Element GetElement(DependencyObject? element)
    {
        Element rv = new();
        if (element is not null &&
            (element is not Freezable freeze || !freeze.IsFrozen))
        {
            rv.Id = DependencyObjectTracker.GetOrSetId(element, KnownElements);
            rv.Type = element.GetType().AssemblyQualifiedName;
        }
        return rv;
    }

    private TElement? GetCachedElement<TElement>(string? id)
        where TElement : DependencyObject
    {
        if (string.IsNullOrWhiteSpace(id)) return default;
        lock (KnownElements)
        {
            if (KnownElements.TryGetValue(id, out WeakReference<DependencyObject>? weakRef) &&
                weakRef.TryGetTarget(out DependencyObject? element))
            {
                return element as TElement;
            }
        }
        return null;
    }
}
#endif