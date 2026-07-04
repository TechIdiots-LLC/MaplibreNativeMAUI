// Data-bound overlay elements for MapLibreMap.
//
// Mirrors Microsoft.Maui.Controls.Maps.Map's ItemsSource / ItemTemplate / ItemTemplateSelector
// (dotnet/maui, MIT License), adapted to the declarative overlay model: each item is materialised
// into a MapOverlayElement (Pin/Polyline/Polygon/Circle) whose BindingContext is the item, then
// added as a child of the map so the existing StyleView machinery renders it into the map surface.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using MapLibreNative.Maui.Handlers.Overlays;

namespace MapLibreNative.Maui.Handlers;

public partial class MapLibreMap
{
    /// <summary>Bindable property for <see cref="ItemsSource"/>.</summary>
    public static readonly BindableProperty ItemsSourceProperty = BindableProperty.Create(
        nameof(ItemsSource), typeof(IEnumerable), typeof(MapLibreMap), default(IEnumerable),
        propertyChanged: (b, o, n) => ((MapLibreMap)b).OnItemsSourcePropertyChanged((IEnumerable?)o, (IEnumerable?)n));

    /// <summary>Bindable property for <see cref="ItemTemplate"/>.</summary>
    public static readonly BindableProperty ItemTemplateProperty = BindableProperty.Create(
        nameof(ItemTemplate), typeof(DataTemplate), typeof(MapLibreMap), default(DataTemplate),
        propertyChanged: (b, o, n) => ((MapLibreMap)b).OnItemTemplatePropertyChanged((DataTemplate?)n));

    /// <summary>Bindable property for <see cref="ItemTemplateSelector"/>.</summary>
    public static readonly BindableProperty ItemTemplateSelectorProperty = BindableProperty.Create(
        nameof(ItemTemplateSelector), typeof(DataTemplateSelector), typeof(MapLibreMap), default(DataTemplateSelector),
        propertyChanged: (b, o, n) => ((MapLibreMap)b).RebuildItems());

    // Overlay elements created from ItemsSource, each paired with its source item so it can be
    // located and removed when the item leaves the collection.
    private readonly List<(object Item, MapOverlayElement Element)> _itemElements = new();

    /// <summary>
    /// A collection of model objects rendered as declarative overlay elements. Each item is
    /// materialised via <see cref="ItemTemplate"/> (or <see cref="ItemTemplateSelector"/>) into a
    /// <see cref="MapOverlayElement"/> (e.g. a <see cref="Pin"/>) whose <c>BindingContext</c> is the
    /// item. If the source implements <see cref="INotifyCollectionChanged"/>, additions and removals
    /// are tracked automatically. Mirrors <c>Microsoft.Maui.Controls.Maps.Map.ItemsSource</c>.
    /// </summary>
    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>
    /// The template used to create an overlay element for each item in <see cref="ItemsSource"/>.
    /// The template must produce a <see cref="MapOverlayElement"/>.
    /// </summary>
    public DataTemplate? ItemTemplate
    {
        get => (DataTemplate?)GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    /// <summary>
    /// Selects a template per item. Set this instead of (not in addition to) <see cref="ItemTemplate"/>
    /// when items need different overlay templates.
    /// </summary>
    public DataTemplateSelector? ItemTemplateSelector
    {
        get => (DataTemplateSelector?)GetValue(ItemTemplateSelectorProperty);
        set => SetValue(ItemTemplateSelectorProperty, value);
    }

    private void OnItemsSourcePropertyChanged(IEnumerable? oldSource, IEnumerable? newSource)
    {
        if (oldSource is INotifyCollectionChanged oldNcc)
            oldNcc.CollectionChanged -= OnItemsSourceCollectionChanged;
        if (newSource is INotifyCollectionChanged newNcc)
            newNcc.CollectionChanged += OnItemsSourceCollectionChanged;
        RebuildItems();
    }

    private void OnItemTemplatePropertyChanged(DataTemplate? newTemplate)
    {
        if (newTemplate is DataTemplateSelector)
            throw new NotSupportedException(
                $"{nameof(MapLibreMap)}.{nameof(ItemTemplate)} does not support a {nameof(DataTemplateSelector)}. " +
                $"Set {nameof(ItemTemplateSelector)} instead.");
        RebuildItems();
    }

    private void OnItemsSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems != null:
                foreach (var item in e.NewItems)
                    CreateItem(item);
                break;

            case NotifyCollectionChangedAction.Remove when e.OldItems != null:
                foreach (var item in e.OldItems)
                    RemoveItem(item);
                break;

            case NotifyCollectionChangedAction.Replace:
                if (e.OldItems != null)
                    foreach (var item in e.OldItems)
                        RemoveItem(item);
                if (e.NewItems != null)
                    foreach (var item in e.NewItems)
                        CreateItem(item);
                break;

            default:
                RebuildItems();
                break;
        }
    }

    private void RebuildItems()
    {
        foreach (var (_, element) in _itemElements)
            Children.Remove(element);
        _itemElements.Clear();

        if (ItemsSource == null || (ItemTemplate == null && ItemTemplateSelector == null))
            return;

        foreach (var item in ItemsSource)
            CreateItem(item);
    }

    private void CreateItem(object item)
    {
        var template = ItemTemplate ?? ItemTemplateSelector?.SelectTemplate(item, this);
        if (template == null)
            return;

        if (template.CreateContent() is not MapOverlayElement element)
            throw new InvalidOperationException(
                $"{nameof(MapLibreMap)}.{nameof(ItemTemplate)} must create a {nameof(MapOverlayElement)} " +
                $"(e.g. {nameof(Pin)}, {nameof(Polyline)}, {nameof(Polygon)} or {nameof(Circle)}).");

        element.BindingContext = item;
        _itemElements.Add((item, element));
        Children.Add(element);
    }

    private void RemoveItem(object item)
    {
        for (int i = _itemElements.Count - 1; i >= 0; i--)
        {
            if (Equals(_itemElements[i].Item, item))
            {
                Children.Remove(_itemElements[i].Element);
                _itemElements.RemoveAt(i);
            }
        }
    }
}
