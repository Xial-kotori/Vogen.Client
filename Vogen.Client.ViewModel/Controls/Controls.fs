﻿namespace Vogen.Client.Controls

open Doaz.Reactive
open Doaz.Reactive.Controls
open Doaz.Reactive.Math
open Newtonsoft.Json
open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Windows
open System.Windows.Controls
open System.Windows.Controls.Primitives
open System.Windows.Input
open System.Windows.Media
open Vogen.Client.Model


type DraggableBase() =
    inherit FrameworkElement()

    member val private MouseDownButton = None with get, set

    override x.OnMouseDown e =
        if x.CaptureMouse() then
            x.MouseDownButton <- Some e.ChangedButton
        base.OnMouseDown e

    override x.OnMouseUp e =
        x.MouseDownButton
        |> Option.filter((=) e.ChangedButton)
        |> Option.iter(fun mouseDownButton ->
            x.ReleaseMouseCapture())
        base.OnMouseUp e

    override x.OnLostMouseCapture e =
        x.MouseDownButton
        |> Option.iter(fun mouseDownButton ->
            x.MouseDownButton <- None)
        base.OnLostMouseCapture e

type SideKeyboard() =
    inherit DraggableBase()

    static let whiteKeyFill = Brushes.White
    static let whiteKeyPen : Pen = Pen(Brushes.Black, 0.6) |>! freeze
    static let blackKeyFill = Brushes.Black
    static let blackKeyPen : Pen = null

    static let keyOffsetLookup = [| -8; 0; -4; 0; 0; -9; 0; -6; 0; -3; 0; 0 |] |> Array.map float       // assuming key height is 12
    static let keyHeightLookup = [| 20; 12; 20; 12; 20; 21; 12; 21; 12; 21; 12; 21 |] |> Array.map float

    member x.BlackKeyLengthRatio
        with get() = x.GetValue SideKeyboard.BlackKeyLengthRatioProperty :?> float
        and set(v : float) = x.SetValue(SideKeyboard.BlackKeyLengthRatioProperty, box v)
    static member val BlackKeyLengthRatioProperty =
        Dp.reg<float, SideKeyboard> "BlackKeyLengthRatio"
            (Dp.Meta(0.6, Dp.MetaFlags.AffectsRender))

    override x.OnRender dc =
        let actualWidth = x.ActualWidth
        let actualHeight = x.ActualHeight
        let keyHeight = ChartProperties.GetKeyHeight x
        let minKey = ChartProperties.GetMinKey x
        let maxKey = ChartProperties.GetMaxKey x
        let vOffset = ChartProperties.GetVOffset x

        let whiteKeyWidth = actualWidth
        let blackKeyWidth = whiteKeyWidth * x.BlackKeyLengthRatio |> clamp 0.0 whiteKeyWidth
        let cornerRadius = 2.0 |> min(half keyHeight) |> min(half blackKeyWidth)

        let botPitch = int(pixelToPitch keyHeight actualHeight vOffset actualHeight) |> max minKey
        let topPitch = int(pixelToPitch keyHeight actualHeight vOffset 0.0) |> min maxKey

        dc.PushClip(RectangleGeometry(Rect(Size(actualWidth, actualHeight))))

        // background
        dc.DrawRectangle(Brushes.Transparent, null, Rect(Size(actualWidth, actualHeight)))

        // white keys
        for pitch in botPitch .. topPitch do
            if not(Midi.isBlackKey pitch) then
                let keyOffset = keyOffsetLookup.[pitch % 12] / 12.0
                let y = pitchToPixel keyHeight actualHeight vOffset (float(pitch + 1) - keyOffset)
                let height = keyHeightLookup.[pitch % 12] / 12.0 * keyHeight
                let x = if isNull whiteKeyPen then 0.0 else half whiteKeyPen.Thickness
                let width = max 0.0 (whiteKeyWidth - x * 2.0)
                dc.DrawRoundedRectangle(whiteKeyFill, whiteKeyPen, Rect(x, y, width, height), cornerRadius, cornerRadius)

        // black keys
        for pitch in botPitch .. topPitch do
            if Midi.isBlackKey pitch then
                let y = pitchToPixel keyHeight actualHeight vOffset (float(pitch + 1))
                let height = keyHeight
                let x = if isNull blackKeyPen then 0.0 else half blackKeyPen.Thickness
                let width = max 0.0 (blackKeyWidth - x * 2.0)
                dc.DrawRoundedRectangle(blackKeyFill, blackKeyPen, Rect(0.0, y, width, height), cornerRadius, cornerRadius)

        // text labels
        for pitch in botPitch .. topPitch do
            if pitch % 12 = 0 then
                let ft = x |> makeFormattedText(sprintf "C%d" (pitch / 12 - 1))
                let x = whiteKeyWidth - 2.0 - ft.Width
                let y = pitchToPixel keyHeight actualHeight vOffset (float(pitch + 1)) + half(keyHeight - ft.Height)
                dc.DrawText(ft, Point(x, y))

type Ruler() =
    inherit DraggableBase()

    static let majorTickHeight = 6.0
    static let minorTickHeight = 4.0

    static let tickPen = Pen(SolidColorBrush((0xFF000000u).AsColor()), 1.0) |>! freeze

    static member MinMajorTickHop = 80.0    // in screen pixels
    static member MinMinorTickHop = 25.0

    static member FindTickHop(timeSig : TimeSignature) quarterWidth minTickHop =
        seq {
            yield 1L
            yield 5L
            yield! Seq.initInfinite(fun i -> 15L <<< i)
                |> Seq.takeWhile(fun length -> length < timeSig.PulsesPerBeat)
            yield timeSig.PulsesPerBeat
            yield! Seq.initInfinite(fun i -> timeSig.PulsesPerMeasure <<< i) }
        |> Seq.find(fun hop ->
            pulseToPixel quarterWidth 0.0 (float hop) >= minTickHop)

    override x.MeasureOverride s =
        let fontFamily = TextBlock.GetFontFamily x
        let fontSize = TextBlock.GetFontSize x
        let height = fontSize * fontFamily.LineSpacing + max majorTickHeight minorTickHeight
        Size(zeroIfInf s.Width, height)

    override x.ArrangeOverride s =
        let fontFamily = TextBlock.GetFontFamily x
        let fontSize = TextBlock.GetFontSize x
        let height = fontSize * fontFamily.LineSpacing + max majorTickHeight minorTickHeight
        Size(s.Width, height)

    override x.OnRender dc =
        let actualWidth = x.ActualWidth
        let actualHeight = x.ActualHeight
        let timeSig = ChartProperties.GetTimeSignature x
        let quarterWidth = ChartProperties.GetQuarterWidth x
        let hOffset = ChartProperties.GetHOffset x

        let minPulse = int64(pixelToPulse quarterWidth hOffset 0.0)
        let maxPulse = int64(pixelToPulse quarterWidth hOffset actualWidth)

        let majorHop = Ruler.FindTickHop timeSig quarterWidth Ruler.MinMajorTickHop
        let minorHop = Ruler.FindTickHop timeSig quarterWidth Ruler.MinMinorTickHop

        dc.PushClip(RectangleGeometry(Rect(Size(actualWidth, actualHeight))))

        // background
        dc.DrawRectangle(Brushes.Transparent, null, Rect(Size(actualWidth, actualHeight)))

        // bottom border
        dc.DrawRectangle(Brushes.Black, null, new Rect(0.0, actualHeight - 0.5, actualWidth, 0.5))

        // tickmarks
        for currPulse in minPulse / minorHop * minorHop .. minorHop .. maxPulse do
            let isMajor = currPulse % majorHop = 0L
            let xPos = pulseToPixel quarterWidth hOffset (float currPulse)
            let height = if isMajor then majorTickHeight else minorTickHeight
            dc.DrawLine(tickPen, Point(xPos, actualHeight - height), Point(xPos, actualHeight))

            if isMajor then
                let textStr =
                    if majorHop % timeSig.PulsesPerMeasure = 0L then MidiTime.formatMeasures timeSig currPulse
                    elif majorHop % timeSig.PulsesPerBeat = 0L then MidiTime.formatMeasureBeats timeSig currPulse
                    else MidiTime.formatFull timeSig currPulse
                let ft = x |> makeFormattedText textStr
                let halfTextWidth = half ft.Width
                if xPos - halfTextWidth >= 0.0 && xPos + halfTextWidth <= actualWidth then
                    dc.DrawText(ft, new Point(xPos - halfTextWidth, 0.0))

type Chart() =
    inherit DraggableBase()

    static let majorTickPen = Pen(SolidColorBrush((0x30000000u).AsColor()), 0.5) |>! freeze
    static let minorTickPen = Pen(SolidColorBrush((0x20000000u).AsColor()), 0.5) |>! freeze
    static let octavePen = Pen(SolidColorBrush((0x20000000u).AsColor()), 0.5) |>! freeze
    static let blackKeyFill = SolidColorBrush((0x10000000u).AsColor()) |>! freeze
    static let playbackCursorPen = Pen(SolidColorBrush((0xFFFF0000u).AsColor()), 0.5) |>! freeze
    static let noteBgPen = Pen(SolidColorBrush((0xFFFFBB77u).AsColor()), 3.0) |>! freeze

    override x.OnRender dc =
        let actualWidth = x.ActualWidth
        let actualHeight = x.ActualHeight
        let timeSig = ChartProperties.GetTimeSignature x
        let quarterWidth = ChartProperties.GetQuarterWidth x
        let keyHeight = ChartProperties.GetKeyHeight x
        let minKey = ChartProperties.GetMinKey x
        let maxKey = ChartProperties.GetMaxKey x
        let hOffset = ChartProperties.GetHOffset x
        let vOffset = ChartProperties.GetVOffset x
        let playbackPos = ChartProperties.GetCursorPosition x

        dc.PushClip(RectangleGeometry(Rect(Size(actualWidth, actualHeight))))

        // background
        dc.DrawRectangle(Brushes.Transparent, null, Rect(Size(actualWidth, actualHeight)))

        // time grids
        let minPulse = int64(pixelToPulse quarterWidth hOffset 0.0)
        let maxPulse = int64(pixelToPulse quarterWidth hOffset actualWidth |> ceil)

        let majorHop = Ruler.FindTickHop timeSig quarterWidth Ruler.MinMajorTickHop
        let minorHop = Ruler.FindTickHop timeSig quarterWidth Ruler.MinMinorTickHop

        for currPulse in minPulse / minorHop * minorHop .. minorHop .. maxPulse do
            let x = pulseToPixel quarterWidth hOffset (float currPulse)
            let pen = if currPulse % majorHop = 0L then majorTickPen else minorTickPen
            dc.DrawLine(pen, Point(x, 0.0), Point(x, actualHeight))

        // pitch grids
        let botPitch = int(pixelToPitch keyHeight actualHeight vOffset actualHeight) |> max minKey
        let topPitch = int(pixelToPitch keyHeight actualHeight vOffset 0.0 |> ceil) |> min maxKey

        for pitch in botPitch .. topPitch do
            match pitch % 12 with
            | 0 | 5 ->
                let y = pitchToPixel keyHeight actualHeight vOffset (float pitch) - half octavePen.Thickness
                dc.DrawLine(octavePen, Point(0.0, y), Point(actualWidth, y))
            | _ -> ()

            if pitch |> Midi.isBlackKey then
                let y = pitchToPixel keyHeight actualHeight vOffset (float(pitch + 1))
                dc.DrawRectangle(blackKeyFill, null, Rect(0.0, y, actualWidth, keyHeight))

        // notes
        let comp = ChartProperties.GetComposition x
        for utt in comp.Utts do
            for note in utt.Notes do
                if note.Off >= minPulse && note.On <= maxPulse && note.Pitch >= botPitch && note.Pitch <= topPitch then
                    let x0 = pulseToPixel quarterWidth hOffset (float note.On)
                    let x1 = pulseToPixel quarterWidth hOffset (float note.Off)
                    let yMid = pitchToPixel keyHeight actualHeight vOffset (float note.Pitch + 0.5)
                    dc.DrawLine(noteBgPen, Point(x0, yMid), Point(x1, yMid))
                    dc.DrawEllipse(noteBgPen.Brush, null, Point(x0, yMid), 5.0, 5.0)
                    if note.Lyric <> "-" then
                        let ft = x |> makeFormattedText note.Lyric
                        dc.DrawText(ft, Point(x0, yMid - ft.Height))
                        let ft = x |> makeFormattedText note.Rom
                        dc.DrawText(ft, Point(x0, yMid))

        dc.Pop()

        // playback cursor
        let xPos = pulseToPixel quarterWidth hOffset (float playbackPos)
        if xPos >= 0.0 && xPos <= actualWidth then
            dc.DrawLine(playbackCursorPen, Point(xPos, 0.0), Point(xPos, actualHeight))



