﻿//------------------------------------------------------------------------------
// <copyright file="MethodTooLongTextAdornment.cs" company="Company">
//     Copyright (c) Company.  All rights reserved.
// </copyright>
//------------------------------------------------------------------------------

using System;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using System.Collections.Generic;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using System.Windows;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio;
using ForceFeedback.Rules.Configuration;
using System.Text;

namespace ForceFeedback.Rules
{
    /// <summary>
    /// MethodTooLongTextAdornment places red boxes behind all the "a"s in the editor window
    /// </summary>
    internal sealed class MethodTooLongTextAdornment
    {
        #region Private Fields

        private IEnumerable<LongMethodOccurrence> _longMethodOccurrences;
        private readonly IAdornmentLayer _layer;
        private readonly IWpfTextView _view;
        private readonly IVsEditorAdaptersFactoryService _adapterService;

        #endregion

        #region Construction

        /// <summary>
        /// Initializes a new instance of the <see cref="MethodTooLongTextAdornment"/> class.
        /// </summary>
        /// <param name="view">Text view to create the adornment for</param>
        /// 
        /// !!! Missing parameter documentation !!!
        ///
        public MethodTooLongTextAdornment(IWpfTextView view, IVsEditorAdaptersFactoryService adapterService)
        {
            if (view == null)
                throw new ArgumentNullException(nameof(view));

            if (adapterService == null)
                throw new ArgumentNullException(nameof(adapterService));

            _layer = view.GetAdornmentLayer("MethodTooLongTextAdornment");

            _view = view;
            _view.LayoutChanged += OnLayoutChanged;
            _view.TextBuffer.Changed += OnTextBufferChanged;

            _adapterService = adapterService;
        }

        #endregion

        #region Event Handler

        private void OnTextBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            // [RS] We do nothing here if the change was caused by ourselves. 
            if (e.EditTag != null && e.EditTag.ToString() == "ForceFeedback")
                return;

            var allowedCharacters = new[]
            {
                "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m", "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z",
                "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
                "0", "1", "2", "3", "4", "5", "6", "7", "8", "9"
            };

            var interestingChangeOccurred = e.Changes.Count > 0 && allowedCharacters.Contains(e.Changes[0].NewText);

            if (!interestingChangeOccurred)
                return;

            var change = e.Changes[0];

            var longMethodOccurence = _longMethodOccurrences
                .Where(occurence => occurence.MethodDeclaration.FullSpan.IntersectsWith(change.NewSpan.Start))
                .Select(occurence => occurence)
                .FirstOrDefault();

            if (longMethodOccurence == null)
                return;

            if (!_view.TextBuffer.CheckEditAccess())
                throw new Exception("Cannot edit text buffer.");

            var replacePattern = longMethodOccurence.LimitConfiguration.ReplacePattern;
            var textToInsert = new StringBuilder(replacePattern.NumberOfReplacementsAtKeystroke);
            var random = new Random();

            for (int index = 1; index <= replacePattern.NumberOfReplacementsAtKeystroke; index++)
            {
                var randomNumber = random.Next(0, replacePattern.ReplacementCharacters.Count());
                textToInsert.Append(replacePattern.ReplacementCharacters[randomNumber]);
            }

            var edit = _view.TextBuffer.CreateEdit(EditOptions.None, null, "ForceFeedback");
            var inserted = edit.Insert(change.NewPosition + 1, textToInsert.ToString());

            if (!inserted)
                throw new Exception($"Cannot insert '{change.NewText}' at position {change.NewPosition} in text buffer.");

            edit.Apply();
        }

        /// <summary>
        /// Handles whenever the text displayed in the view changes by adding the adornment to any reformatted lines.
        /// </summary>
        /// <remarks><para>This event is raised whenever the rendered text displayed in the <see cref="ITextView"/> changes.</para>
        /// <para>It is raised whenever the view does a layout (which happens when DisplayTextLineContainingBufferPosition is called or in response to text or classification changes).</para>
        /// <para>It is also raised whenever the view scrolls horizontally or when its size changes.</para>
        /// </remarks>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        internal async void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            try
            {
                var methodDeclarations = await CollectMethodDeclarationSyntaxNodes(e.NewSnapshot);
                var longMethodOccurrences = AnalyzeLongMethodOccurrences(methodDeclarations);

                CreateVisualsForLongMethods(longMethodOccurrences);

                // [RS] Cache the occurrences for later use.
                _longMethodOccurrences = longMethodOccurrences;
            }
            catch
            {
                // [RS] Maybe we should handle this exception a bit more faithfully. For now, we ignore the exceptions here 
                //      and wait for the next LayoutChanged event
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// This method checks the given method declarations are too long based on the configured limites. If so, the method 
        /// declaration and the corresponding limit configuration is put together in an instance of  <see cref="LongMethodOccurrence">LongMethodOccurrence</see>.
        /// </summary>
        /// <param name="methodDeclarations">The list of method declarations that will be analyzed.</param>
        /// <returns>Returns a list of occurrences and their limit configuration.</returns>
        private IEnumerable<LongMethodOccurrence> AnalyzeLongMethodOccurrences(IEnumerable<MethodDeclarationSyntax> methodDeclarations)
        {
            if (methodDeclarations == null)
                throw new ArgumentNullException(nameof(methodDeclarations));

            var result = new List<LongMethodOccurrence>();

            foreach (var methodDeclaration in methodDeclarations)
            {
                var linesOfCode = methodDeclaration.Body.WithoutLeadingTrivia().WithoutTrailingTrivia().GetText().Lines.Count;

                LongMethodLimitConfiguration correspondingLimitConfiguration = null;

                foreach (var limitConfiguration in ConfigurationManager.Configuration.MethodTooLongLimits.OrderBy(limit => limit.Lines))
                {
                    if (linesOfCode < limitConfiguration.Lines)
                        break;
                    
                    correspondingLimitConfiguration = limitConfiguration;
                }

                if (correspondingLimitConfiguration != null)
                    result.Add(new LongMethodOccurrence(methodDeclaration, correspondingLimitConfiguration));
            }

            return result;
        }

        /// <summary>
        /// This method collects syntax nodes of method declarations that have too many lines of code.
        /// </summary>
        /// <param name="newSnapshot">The text snapshot containing the code to analyze.</param>
        /// <returns>Returns a list with the method declaration nodes.</returns>
        private async Task<IEnumerable<MethodDeclarationSyntax>> CollectMethodDeclarationSyntaxNodes(ITextSnapshot newSnapshot)
        {
            if (newSnapshot == null)
                throw new ArgumentNullException(nameof(newSnapshot));

            var currentDocument = newSnapshot.GetOpenDocumentInCurrentContextWithChanges();

            var syntaxRoot = await currentDocument.GetSyntaxRootAsync();

            var tooLongMethodDeclarations = syntaxRoot
                .DescendantNodes(node => true, false)
                .Where(node => node.Kind() == SyntaxKind.MethodDeclaration)
                .Select(methodDeclaration => methodDeclaration as MethodDeclarationSyntax);

            return tooLongMethodDeclarations;
        }

        /// <summary>
        /// Adds a background behind the methods that have too many lines.
        /// </summary>
        /// <param name="occurrences">A list of long method occurences for which the visuals will be created.</param>
        private void CreateVisualsForLongMethods(IEnumerable<LongMethodOccurrence> occurrences)
        {
            if (occurrences == null)
                throw new ArgumentNullException(nameof(occurrences));

            foreach (var occurrence in occurrences)
            {
                var methodDeclaration = occurrence.MethodDeclaration;
                var snapshotSpan = new SnapshotSpan(_view.TextSnapshot, Span.FromBounds(methodDeclaration.Span.Start, methodDeclaration.Span.Start + methodDeclaration.Span.Length));
                var adornmentBounds = CalculateBounds(methodDeclaration, snapshotSpan);

                if (adornmentBounds.IsEmpty)
                    continue;

                var image = CreateAndPositionMethodBackgroundVisual(adornmentBounds, occurrence);

                if (image == null)
                    continue;

                _layer.RemoveAdornmentsByVisualSpan(snapshotSpan);
                _layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, snapshotSpan, methodDeclaration, image, null);
            }
        }

        /// <summary>
        /// This method creates the visual for a method background and moves it to the correct position.
        /// </summary>
        /// <param name="adornmentBounds">The bounds of the rectangular adornment.</param>
        /// <param name="longMethodOccurence">The occurence of the method declaration for which the visual will be created.</param>
        /// <returns>Returns the image that is the visual adornment (method background).</returns>
        private Image CreateAndPositionMethodBackgroundVisual(Rect adornmentBounds, LongMethodOccurrence longMethodOccurence)
        {
            if (adornmentBounds == null)
                throw new ArgumentNullException(nameof(adornmentBounds));

            if (longMethodOccurence == null)
                throw new ArgumentNullException(nameof(longMethodOccurence));

            var backgroundGeometry = new RectangleGeometry(adornmentBounds);

            var backgroundBrush = new SolidColorBrush(longMethodOccurence.LimitConfiguration.Color);
            backgroundBrush.Freeze();

            var drawing = new GeometryDrawing(backgroundBrush, ConfigurationManager.LongMethodBorderPen, backgroundGeometry);
            drawing.Freeze();

            var drawingImage = new DrawingImage(drawing);
            drawingImage.Freeze();

            var image = new Image
            {
                Source = drawingImage
            };

            Canvas.SetLeft(image, adornmentBounds.Left);
            Canvas.SetTop(image, adornmentBounds.Top);

            return image;
        }

        /// <summary>
        /// This method calculates the bounds of the method background adornment.
        /// </summary>
        /// <param name="methodDeclarationSyntaxNode">The syntax node that represents the method declaration that has too many lines of code.</param>
        /// <param name="snapshotSpan">The span of text that is associated with the background adornment.</param>
        /// <returns>Returns the calculated bounds of the method adornment.</returns>
        private Rect CalculateBounds(MethodDeclarationSyntax methodDeclarationSyntaxNode, SnapshotSpan snapshotSpan)
        {
            if (methodDeclarationSyntaxNode == null)
                throw new ArgumentNullException(nameof(methodDeclarationSyntaxNode));

            if (snapshotSpan == null)
                throw new ArgumentNullException(nameof(snapshotSpan));

            var nodes = new List<SyntaxNode>(methodDeclarationSyntaxNode.ChildNodes());
            nodes.Add(methodDeclarationSyntaxNode);

            var nodesFirstCharacterPositions = nodes.Select(node => node.Span.Start);
            var coordinatesOfCharacterPositions = new List<double>();

            foreach (var position in nodesFirstCharacterPositions)
            {
                var point = CalculateScreenCoordinatesForCharacterPosition(position);
                coordinatesOfCharacterPositions.Add(point.x);
            }

            // [RS] In the case we cannot find the screen coordinates for a character position, we simply skip and return empty bounds.
            if (coordinatesOfCharacterPositions == null || coordinatesOfCharacterPositions.Count == 0)
                return Rect.Empty;

            var viewOffset = VisualTreeHelper.GetOffset(_view.VisualElement);

            var left = coordinatesOfCharacterPositions
                .Select(coordinate => coordinate)
                .Min() - viewOffset.X;
            
            var geometry = _view.TextViewLines.GetMarkerGeometry(snapshotSpan, true, new Thickness(0));
            
            if (geometry == null)
                return Rect.Empty;

            var top = geometry.Bounds.Top;
            var width = geometry.Bounds.Right - geometry.Bounds.Left; // - viewOffset.X;
            var height = geometry.Bounds.Bottom - geometry.Bounds.Top;
            
            return new Rect(left, top, width, height);
        }

        /// <summary>
        /// This method tries to calculate the screen coordinates of a specific character position in the stream.
        /// </summary>
        /// <param name="position">The position of the character in the stream.</param>
        /// <returns>Returns a point representing the coordinates.</returns>
        private POINT CalculateScreenCoordinatesForCharacterPosition(int position)
        {
            try
            {
                var line = 0;
                var column = 0;
                var point = new POINT[1];
                var textView = _adapterService.GetViewAdapter(_view as ITextView);
                var result = textView.GetLineAndColumn(position, out line, out column);
                
                // [RS] If the line and column of a text position from the stream cannot be calculated, we simply return a zero-point.
                //      Maybe we should handle the error case slightly more professional by write some log entries or so.
                if (result != VSConstants.S_OK)
                    return new POINT() { x = 0, y = 0 };

                result = textView.GetPointOfLineColumn(line, column, point);

                return point[0];
            }
            catch
            {
                // [RS] In any case of error we simply return a zero-point.
                //      Maybe we should handle this exception slightly more professional by write some log entries or so.
                return new POINT() { x = 0, y = 0 };
            }
        }

        private void LoadConfiguration()
        {
            //SettingsManager settingsManager = new ShellSettingsManager(_serviceProvider);
            //WritableSettingsStore userSettingsStore = settingsManager.GetWritableSettingsStore(SettingsScope.UserSettings);

            // Find out whether Notepad is already an External Tool.
            //int toolCount = userSettingsStore.GetInt32(("External Tools", "ToolNumKeys");
            //bool hasNotepad = false;
            //CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;
            //for (int i = 0; i < toolCount; i++)
            //{
            //    if (Compare.IndexOf(userSettingsStore.GetString("External Tools", "ToolCmd" + i), "Notepad", CompareOptions.IgnoreCase) >= 0)
            //    {
            //        hasNotepad = true;
            //        break;
            //    }
            //}
        }

        #endregion
    }
}
