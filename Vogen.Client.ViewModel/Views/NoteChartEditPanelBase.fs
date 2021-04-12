﻿namespace Vogen.Client.Views

open Doaz.Reactive
open Doaz.Reactive.Controls
open Doaz.Reactive.Math
open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Windows
open System.Windows.Controls
open System.Windows.Controls.Primitives
open System.Windows.Input
open Vogen.Client.Controls
open Vogen.Client.Model
open Vogen.Client.ViewModel


type ChartMouseEvent =
    | ChartMouseDown of e : MouseButtonEventArgs
    | ChartMouseMove of e : MouseEventArgs
    | ChartMouseRelease of e : MouseEventArgs
    | ChartMouseEnter of e : MouseEventArgs
    | ChartMouseLeave of e : MouseEventArgs

    static member BindEvents push (x : NoteChartEditBase) =
        x.MouseDown.Add(fun e ->
            push(ChartMouseDown e)
            e.Handled <- true)

        x.MouseMove.Add(fun e ->
            push(ChartMouseMove e)
            e.Handled <- true)

        x.LostMouseCapture.Add(fun e ->
            push(ChartMouseRelease e)
            e.Handled <- true)

        x.MouseEnter.Add(fun e ->
            push(ChartMouseEnter e)
            e.Handled <- true)

        x.MouseLeave.Add(fun e ->
            push(ChartMouseLeave e)
            e.Handled <- true)

type NoteChartEditPanelBase() =
    inherit UserControl()

    member x.Quantization
        with get() = x.GetValue NoteChartEditPanelBase.QuantizationProperty :?> int64
        and set(v : int64) = x.SetValue(NoteChartEditPanelBase.QuantizationProperty, box v)
    static member val QuantizationProperty =
        Dp.reg<int64, NoteChartEditPanelBase> "Quantization"
            (Dp.Meta(Midi.ppqn / 2L, Dp.MetaFlags.AffectsRender))

    member x.Snap
        with get() = x.GetValue NoteChartEditPanelBase.SnapProperty :?> bool
        and set(v : bool) = x.SetValue(NoteChartEditPanelBase.SnapProperty, box v)
    static member val SnapProperty =
        Dp.reg<bool, NoteChartEditPanelBase> "Snap"
            (Dp.Meta(true, Dp.MetaFlags.AffectsRender))

    member x.ProgramModel = x.DataContext :?> ProgramModel

    abstract ChartEditor : ChartEditor
    default x.ChartEditor = Unchecked.defaultof<_>
    abstract ChartEditorAdornerLayer : ChartEditorAdornerLayer
    default x.ChartEditorAdornerLayer = Unchecked.defaultof<_>
    abstract RulerGrid : RulerGrid
    default x.RulerGrid = Unchecked.defaultof<_>
    abstract SideKeyboard : SideKeyboard
    default x.SideKeyboard = Unchecked.defaultof<_>
    abstract HScrollZoom : ChartScrollZoomKitBase
    default x.HScrollZoom = Unchecked.defaultof<_>
    abstract VScrollZoom : ChartScrollZoomKitBase
    default x.VScrollZoom = Unchecked.defaultof<_>
    abstract LyricPopup : Popup
    default x.LyricPopup = Unchecked.defaultof<_>
    abstract LyricTextBox : TextBox
    default x.LyricTextBox = Unchecked.defaultof<_>

    member x.BindBehaviors() =
        let rec mouseMidDownDragging(prevMousePos : Point, idle)(edit : NoteChartEditBase) = behavior {
            match! () with
            | ChartMouseMove e ->
                let hOffset = edit.HOffsetAnimated
                let vOffset = edit.VOffsetAnimated
                let quarterWidth = edit.QuarterWidth
                let keyHeight = edit.KeyHeight

                let mousePos = e.GetPosition edit
                if edit.CanScrollH then
                    let xDelta = pixelToPulse quarterWidth 0.0 (mousePos.X - prevMousePos.X)
                    x.HScrollZoom.EnableAnimation <- false
                    x.HScrollZoom.ScrollValue <- hOffset - xDelta
                    x.HScrollZoom.EnableAnimation <- true
                if edit.CanScrollV then
                    let yDelta = pixelToPitch keyHeight 0.0 0.0 (mousePos.Y - prevMousePos.Y)
                    x.VScrollZoom.EnableAnimation <- false
                    x.VScrollZoom.ScrollValue <- vOffset - yDelta
                    x.VScrollZoom.EnableAnimation <- true

                return! edit |> mouseMidDownDragging(mousePos, idle)

            | ChartMouseRelease e -> return! idle()

            | _ -> return! edit |> mouseMidDownDragging(prevMousePos, idle) }

        let findMouseOverNote(mousePos : Point)(edit : ChartEditor) =
            let actualHeight = edit.ActualHeight
            let quarterWidth = edit.QuarterWidth
            let keyHeight = edit.KeyHeight
            let hOffset = edit.HOffsetAnimated
            let vOffset = edit.VOffsetAnimated
            let mousePulse = pixelToPulse quarterWidth hOffset mousePos.X |> int64
            let mousePitch = pixelToPitch keyHeight actualHeight vOffset mousePos.Y |> round |> int

            let comp = !!x.ProgramModel.ActiveComp

            Seq.tryHead <| seq {
                for uttIndex in comp.Utts.Length - 1 .. -1 .. 0 do
                    let utt = comp.Utts.[uttIndex]
                    for noteIndex in utt.Notes.Length - 1 .. -1 .. 0 do
                        let note = utt.Notes.[noteIndex]
                        if mousePulse |> between note.On note.Off && mousePitch = note.Pitch then
                            yield utt, note }
            |> Option.map(fun (utt, note) ->
                let x0 = pulseToPixel quarterWidth hOffset (float note.On)
                let x1 = pulseToPixel quarterWidth hOffset (float note.Off)
                let noteDragType =
                    if   mousePos.X <= min(x0 + 6.0)(lerp x0 x1 0.2) then NoteDragResizeLeft
                    elif mousePos.X >= max(x1 - 6.0)(lerp x0 x1 0.8) then NoteDragResizeRight
                    else NoteDragMove
                utt, note, noteDragType)

        let findMouseOverNoteOp mousePosOp edit =
            mousePosOp |> Option.bind(fun mousePos -> findMouseOverNote mousePos edit)

        let quantize snap quantization (timeSig : TimeSignature) pulses =
            if not snap then pulses else
                let pulsesMeasureQuantized = pulses / timeSig.PulsesPerMeasure * timeSig.PulsesPerMeasure
                pulsesMeasureQuantized + (pulses - pulsesMeasureQuantized) / quantization * quantization

        let quantizeCeil snap quantization (timeSig : TimeSignature) pulses =
            if not snap then pulses else
                let pulsesMeasureQuantized = pulses / timeSig.PulsesPerMeasure * timeSig.PulsesPerMeasure
                pulsesMeasureQuantized + ((pulses - pulsesMeasureQuantized) /^ quantization * quantization |> min timeSig.PulsesPerMeasure)

        let getPlaybackCursorPos(mousePos : Point)(edit : NoteChartEditBase) =
            let hOffset = edit.HOffsetAnimated
            let quarterWidth = edit.QuarterWidth
            let comp = !!x.ProgramModel.ActiveComp
            let quantization = x.Quantization
            let snap = x.Snap

            let newCursorPos = int64(pixelToPulse quarterWidth hOffset mousePos.X) |> NoteChartEditBase.CoerceCursorPosition edit
            newCursorPos |> quantize snap quantization comp.TimeSig0

        let updatePlaybackCursorPos(e : MouseEventArgs)(edit : NoteChartEditBase) =
            let mousePos = e.GetPosition edit
            let newCursorPos = getPlaybackCursorPos mousePos edit
            x.ProgramModel.ManualSetCursorPos newCursorPos

        let updateMouseOverCursorPos mousePosOp (edit : NoteChartEditBase) =
            let mouseOverCursorPosOp = mousePosOp |> Option.map(fun (mousePos : Point) -> getPlaybackCursorPos mousePos edit)
            x.ChartEditorAdornerLayer.MouseOverCursorPositionOp <- mouseOverCursorPosOp

        x.ChartEditor |> ChartMouseEvent.BindEvents(
            let edit = x.ChartEditor

            let updateMouseOverNote mousePosOp =
                let mouseOverNoteOp = findMouseOverNoteOp mousePosOp edit
                x.ChartEditorAdornerLayer.MouseOverNoteOp <- mouseOverNoteOp

            let rec idle() = behavior {
                match! () with
                | ChartMouseDown e ->
                    match e.ChangedButton with
                    | MouseButton.Left ->
                        updateMouseOverNote None
                        let comp = !!x.ProgramModel.ActiveComp
                        let selection = !!x.ProgramModel.ActiveSelection
                        let mousePos = e.GetPosition edit
                        let mouseDownNoteOp = findMouseOverNote mousePos edit
                        match mouseDownNoteOp with
                        | None ->
                            if e.ClickCount = 2 then
                                x.ProgramModel.ActiveSelection |> Rp.modify(fun selection ->
                                    selection.SetActiveUtt None)

                            match Keyboard.Modifiers with
                            | ModifierKeys.Control -> ()
                            | _ ->
                                x.ProgramModel.ActiveSelection |> Rp.modify(fun selection ->
                                    selection.SetSelectedNotes ImmutableHashSet.Empty)

                            let mouseDownSelection = !!x.ProgramModel.ActiveSelection
                            return! draggingSelBox mouseDownSelection mousePos

                        | Some(utt, note, noteDragType) when e.ClickCount >= 2 ->
                            let actualHeight = edit.ActualHeight
                            let quarterWidth = edit.QuarterWidth
                            let keyHeight = edit.KeyHeight
                            let hOffset = edit.HOffsetAnimated
                            let vOffset = edit.VOffsetAnimated
                            let x0 = pulseToPixel quarterWidth hOffset (float note.On)
                            let x1 = pulseToPixel quarterWidth hOffset (float note.Off)
                            let yMid = pitchToPixel keyHeight actualHeight vOffset (float note.Pitch)
                            x.LyricPopup.PlacementRectangle <- Rect(x0, yMid - half keyHeight, x1 - x0, keyHeight)
                            x.LyricPopup.IsOpen <- true
                            x.LyricTextBox.Text <- note.Rom + $" - {e.ClickCount}"
                            x.LyricTextBox.SelectAll()
                            x.LyricTextBox.Focus() |> ignore
                            return! idle()

                        | Some(utt, note, noteDragType) ->
                            let quarterWidth = edit.QuarterWidth
                            let hOffset = edit.HOffsetAnimated
                            let mousePulse = pixelToPulse quarterWidth hOffset mousePos.X |> int64

                            let quantization = x.Quantization
                            let snap = x.Snap
                            let mouseDownPulse =
                                match noteDragType with
                                | NoteDragResizeLeft
                                | NoteDragMove ->
                                    let noteGridOffset = note.On - (note.On |> quantize snap quantization comp.TimeSig0)
                                    (mousePulse - noteGridOffset |> quantize snap quantization comp.TimeSig0) + noteGridOffset
                                | NoteDragResizeRight ->
                                    let noteGridOffset = note.Off - (note.Off |> quantizeCeil snap quantization comp.TimeSig0)
                                    (mousePulse - noteGridOffset |> quantizeCeil snap quantization comp.TimeSig0) + noteGridOffset

                            //MidiPlayback.playPitch note.Pitch
                            x.ProgramModel.ActiveSelection |> Rp.modify(fun selection ->
                                selection.SetActiveUtt(Some utt))

                            if not(selection.GetIsNoteSelected note) then
                                if Keyboard.Modifiers = ModifierKeys.Control then
                                    x.ProgramModel.ActiveSelection |> Rp.modify(fun selection ->
                                        selection.UpdateSelectedNotes(fun selectedNotes ->
                                            selectedNotes.Add note))
                                else
                                    x.ProgramModel.ActiveSelection |> Rp.modify(fun selection ->
                                        selection.SetSelectedNotes(
                                            ImmutableHashSet.Create note))

                            let mouseDownSelection = !!x.ProgramModel.ActiveSelection
                            let mouseDownSelectedNotes = mouseDownSelection.SelectedNotes.Intersect comp.AllNotes
                            let dragNoteArgs = note, comp, mouseDownSelectedNotes, mouseDownPulse, noteDragType

                            let isPendingDeselect = selection.GetIsNoteSelected note && Keyboard.Modifiers = ModifierKeys.Control
                            if isPendingDeselect then
                                return! mouseDownNotePendingDeselect dragNoteArgs
                            else
                                x.ProgramModel.UndoRedoStack.PushUndo(
                                    MouseDragNote noteDragType, (comp, mouseDownSelection), (comp, mouseDownSelection))
                                return! draggingNote dragNoteArgs

                    | MouseButton.Middle ->
                        updateMouseOverNote None
                        return! edit |> mouseMidDownDragging(e.GetPosition edit, idle)

                    | _ -> return! idle()

                | ChartMouseMove e ->
                    updateMouseOverNote(Some(e.GetPosition edit))
                    return! idle()

                | _ -> return! idle() }

            and mouseDownNotePendingDeselect dragNoteArgs = behavior {
                let mouseDownNote, mouseDownComp, mouseDownSelectedNotes, mouseDownPulse, noteDragType = dragNoteArgs
                match! () with
                | ChartMouseMove e ->
                    let mouseDownSelection = !!x.ProgramModel.ActiveSelection
                    x.ProgramModel.UndoRedoStack.PushUndo(
                        MouseDragNote noteDragType, (mouseDownComp, mouseDownSelection), (mouseDownComp, mouseDownSelection))
                    return! (draggingNote dragNoteArgs).Run(ChartMouseMove e)

                | ChartMouseRelease e ->
                    x.ProgramModel.ActiveSelection |> Rp.modify(fun selection ->
                        selection.UpdateSelectedNotes(fun selectedNotes ->
                            selectedNotes.Remove mouseDownNote))
                    updateMouseOverNote(Some(e.GetPosition edit))
                    return! idle()

                | _ -> return! mouseDownNotePendingDeselect dragNoteArgs }

            and draggingNote dragNoteArgs = behavior {
                let mouseDownNote, mouseDownComp, mouseDownSelectedNotes, mouseDownPulse, noteDragType = dragNoteArgs
                match! () with
                | ChartMouseMove e ->
                    let actualHeight = edit.ActualHeight
                    let quarterWidth = edit.QuarterWidth
                    let keyHeight = edit.KeyHeight
                    let minKey = edit.MinKey
                    let maxKey = edit.MaxKey
                    let hOffset = edit.HOffsetAnimated
                    let vOffset = edit.VOffsetAnimated
                    let comp = !!x.ProgramModel.ActiveComp
                    let mousePos = e.GetPosition edit
                    let mousePulse = pixelToPulse quarterWidth hOffset mousePos.X |> int64
                    let mousePitch = pixelToPitch keyHeight actualHeight vOffset mousePos.Y |> round |> int

                    let quantization = x.Quantization
                    let snap = x.Snap
                    let newNoteOn =
                        match noteDragType with
                        | NoteDragResizeLeft
                        | NoteDragMove ->
                            mouseDownNote.On + mousePulse - mouseDownPulse |> quantize snap quantization comp.TimeSig0
                        | NoteDragResizeRight ->
                            mouseDownNote.Off + mousePulse - mouseDownPulse |> quantizeCeil snap quantization comp.TimeSig0

                    let deltaPulse, deltaDur =
                        let selMinPulse = mouseDownSelectedNotes |> Seq.map(fun note -> note.On) |> Seq.min
                        let selMinDur   = mouseDownSelectedNotes |> Seq.map(fun note -> note.Dur) |> Seq.min
                        match noteDragType with
                        | NoteDragResizeLeft ->
                            let minOn = mouseDownNote.On - selMinPulse
                            let maxOn = mouseDownNote.On + selMinDur - 1L |> quantize snap quantization comp.TimeSig0
                            let deltaPulse = (newNoteOn |> clamp minOn maxOn) - mouseDownNote.On
                            deltaPulse, -deltaPulse
                        | NoteDragMove ->
                            let minOn = mouseDownNote.On - selMinPulse
                            (newNoteOn |> max minOn) - mouseDownNote.On, 0L
                        | NoteDragResizeRight ->
                            let minOff = mouseDownNote.Off - selMinDur + 1L |> quantizeCeil snap quantization comp.TimeSig0
                            0L, (newNoteOn |> max minOff) - mouseDownNote.Off

                    let deltaPitch =
                        match noteDragType with
                        | NoteDragResizeLeft
                        | NoteDragResizeRight -> 0
                        | NoteDragMove ->
                            let mouseDownSelMinPitch = mouseDownSelectedNotes |> Seq.map(fun note -> note.Pitch) |> Seq.min
                            let mouseDownSelMaxPitch = mouseDownSelectedNotes |> Seq.map(fun note -> note.Pitch) |> Seq.max
                            mousePitch - mouseDownNote.Pitch |> clamp(minKey - mouseDownSelMinPitch)(maxKey - mouseDownSelMaxPitch)

                    if deltaPulse = 0L && deltaDur = 0L && deltaPitch = 0 then
                        x.ProgramModel.ActiveComp |> Rp.set mouseDownComp
                        x.ProgramModel.ActiveSelection |> Rp.modify(fun selection ->
                            selection.SetSelectedNotes mouseDownSelectedNotes)

                    else
                        let selectedNotesDict = mouseDownSelectedNotes.ToImmutableDictionary(id, fun (note : Note) ->
                            note.Move(note.Pitch + deltaPitch, note.On + deltaPulse, note.Dur + deltaDur))

                        let newUtts = ImmutableArray.CreateRange(mouseDownComp.Utts, fun utt ->
                            if utt.Notes |> Seq.forall(fun note -> not(selectedNotesDict.ContainsKey note)) then utt else
                                utt.SetNotes(ImmutableArray.CreateRange(utt.Notes, fun note ->
                                    selectedNotesDict.TryGetValue note
                                    |> Option.ofByRef
                                    |> Option.defaultValue note)))

                        x.ProgramModel.ActiveComp |> Rp.modify(fun comp -> comp.SetUtts newUtts)
                        x.ProgramModel.ActiveSelection |> Rp.modify(fun selection ->
                            selection.SetSelectedNotes(ImmutableHashSet.CreateRange selectedNotesDict.Values))

                    x.ProgramModel.UndoRedoStack.UpdateLatestRedo((!!x.ProgramModel.ActiveComp, !!x.ProgramModel.ActiveSelection))

                    return! draggingNote dragNoteArgs

                | ChartMouseRelease e ->
                    updateMouseOverNote(Some(e.GetPosition edit))
                    return! idle()

                | _ -> return! draggingNote dragNoteArgs }

            and draggingSelBox mouseDownSelection mouseDownPos = behavior {
                match! () with
                | ChartMouseMove e ->
                    let actualHeight = edit.ActualHeight
                    let quarterWidth = edit.QuarterWidth
                    let keyHeight = edit.KeyHeight
                    let hOffset = edit.HOffsetAnimated
                    let vOffset = edit.VOffsetAnimated
                    let comp = !!x.ProgramModel.ActiveComp
                    let mousePos = e.GetPosition edit

                    let selMinPulse = pixelToPulse quarterWidth hOffset (min mousePos.X mouseDownPos.X) |> int64
                    let selMaxPulse = pixelToPulse quarterWidth hOffset (max mousePos.X mouseDownPos.X) |> int64
                    let selMinPitch = pixelToPitch keyHeight actualHeight vOffset (max mousePos.Y mouseDownPos.Y) |> round |> int
                    let selMaxPitch = pixelToPitch keyHeight actualHeight vOffset (min mousePos.Y mouseDownPos.Y) |> round |> int
                    x.ChartEditorAdornerLayer.SelectionBoxOp <- Some(selMinPulse, selMaxPulse, selMinPitch, selMaxPitch)

                    let selection =
                        mouseDownSelection.SetSelectedNotes(
                            comp.AllNotes
                            |> Seq.filter(fun note ->
                                let noteHasIntersection =
                                    note.On <= selMaxPulse && note.Off >= selMinPulse && note.Pitch |> betweenInc selMinPitch selMaxPitch
                                noteHasIntersection <> mouseDownSelection.GetIsNoteSelected note)
                            |> ImmutableHashSet.CreateRange)
                    x.ProgramModel.ActiveSelection |> Rp.set selection

                    return! draggingSelBox mouseDownSelection mouseDownPos

                | ChartMouseRelease e ->
                    x.ChartEditorAdornerLayer.SelectionBoxOp <- None
                    updateMouseOverNote(Some(e.GetPosition edit))
                    return! idle()

                | _ -> return! draggingSelBox mouseDownSelection mouseDownPos }

            Behavior.agent(idle()))

        x.RulerGrid |> ChartMouseEvent.BindEvents(
            let edit = x.RulerGrid

            let rec idle() = behavior {
                match! () with
                | ChartMouseDown e ->
                    match e.ChangedButton with
                    | MouseButton.Left ->
                        edit |> updateMouseOverCursorPos None
                        edit |> updatePlaybackCursorPos e
                        return! mouseLeftDown()
                    | MouseButton.Middle ->
                        edit |> updateMouseOverCursorPos None
                        return! edit |> mouseMidDownDragging(e.GetPosition edit, idle)
                    | _ -> return! idle()
                | ChartMouseMove e ->
                    edit |> updateMouseOverCursorPos(Some(e.GetPosition edit))
                    return! idle()
                | ChartMouseLeave e ->
                    edit |> updateMouseOverCursorPos None
                    return! idle()
                | _ -> return! idle() }

            and mouseLeftDown() = behavior {
                match! () with
                | ChartMouseMove e ->
                    edit |> updatePlaybackCursorPos e
                    return! mouseLeftDown()
                | ChartMouseRelease e ->
                    edit |> updateMouseOverCursorPos(Some(e.GetPosition edit))
                    return! idle()
                | _ -> return! mouseLeftDown() }

            Behavior.agent(idle()))

        x.SideKeyboard |> ChartMouseEvent.BindEvents(
            let edit = x.SideKeyboard

            let rec idle() = behavior {
                match! () with
                | ChartMouseDown e ->
                    match e.ChangedButton with
                    | MouseButton.Middle ->
                        return! edit |> mouseMidDownDragging(e.GetPosition edit, idle)
                    | _ -> return! idle()
                | _ -> return! idle() }

            Behavior.agent(idle()))

        // mouse wheel events
        let onMouseWheel(edit : NoteChartEditBase)(e : MouseWheelEventArgs) =
            if edit.CanScrollH then
                let zoomDelta = float(sign e.Delta) * 0.2       // TODO Use Slider.SmallChange
                let log2Zoom = x.HScrollZoom.Log2ZoomValue
                let log2ZoomMin = x.HScrollZoom.Log2ZoomMinimum
                let log2ZoomMax = x.HScrollZoom.Log2ZoomMaximum
                let newLog2Zoom = log2Zoom + zoomDelta |> clamp log2ZoomMin log2ZoomMax
                let mousePos = e.GetPosition edit
                let xPos = mousePos.X
                let hOffset = x.HScrollZoom.ScrollValue
                let quarterWidth = 2.0 ** log2Zoom
                let newQuarterWidth = 2.0 ** newLog2Zoom
                let currPulse = pixelToPulse quarterWidth hOffset xPos
                let nextPulse = pixelToPulse newQuarterWidth hOffset xPos
                let offsetDelta = nextPulse - currPulse

                x.HScrollZoom.Log2ZoomValue <- newLog2Zoom
                x.HScrollZoom.ScrollValue <- hOffset - offsetDelta

            elif edit.CanScrollV then
                let zoomDelta = float(sign e.Delta) * 0.1       // TODO Use Slider.SmallChange
                let log2Zoom = x.VScrollZoom.Log2ZoomValue
                let log2ZoomMin = x.VScrollZoom.Log2ZoomMinimum
                let log2ZoomMax = x.VScrollZoom.Log2ZoomMaximum
                let newLog2Zoom = log2Zoom + zoomDelta |> clamp log2ZoomMin log2ZoomMax
                let mousePos = e.GetPosition edit
                let yPos = mousePos.Y
                let vOffset = x.VScrollZoom.ScrollValue
                let keyHeight = 2.0 ** log2Zoom
                let newKeyHeight = 2.0 ** newLog2Zoom
                let actualHeight = x.ChartEditor.ActualHeight
                let currPitch = pixelToPitch keyHeight actualHeight vOffset yPos
                let nextPitch = pixelToPitch newKeyHeight actualHeight vOffset yPos
                let offsetDelta = nextPitch - currPitch

                x.VScrollZoom.Log2ZoomValue <- newLog2Zoom
                x.VScrollZoom.ScrollValue <- vOffset - offsetDelta

        x.ChartEditor.MouseWheel.Add(onMouseWheel x.ChartEditor)
        x.RulerGrid.MouseWheel.Add(onMouseWheel x.RulerGrid)
        x.SideKeyboard.MouseWheel.Add(onMouseWheel x.SideKeyboard)

        // playback cursor
        x.ChartEditor.CursorPositionChanged.Add <| fun (prevPlayPos, playPos) ->
            let edit = x.ChartEditor
            if edit.IsPlaying then
                let quarterWidth = edit.QuarterWidth
                let hOffset = x.HScrollZoom.ScrollValue
                let actualWidth = edit.ActualWidth
                let hRightOffset = pixelToPulse quarterWidth hOffset actualWidth
                if float prevPlayPos < hRightOffset && float playPos >= hRightOffset then
                    x.HScrollZoom.ScrollValue <- hOffset + (hRightOffset - hOffset) * 0.9

        // key events
        x.KeyDown.Add <| fun e ->
            match e.Key with
            | Key.Space ->
                let programModel = x.ProgramModel
                if not !!programModel.IsPlaying then
                    programModel.Play()
                else
                    programModel.Stop()

            | Key.Delete ->
                let comp = !!x.ProgramModel.ActiveComp
                let selection = !!x.ProgramModel.ActiveSelection
                let mouseDownSelection = selection.SelectedNotes.Intersect comp.AllNotes
                if not mouseDownSelection.IsEmpty then
                    let uttToNewDict =
                        comp.Utts
                        |> Seq.choose(fun utt ->
                            let newNotes = utt.Notes.RemoveAll(Predicate(selection.GetIsNoteSelected))
                            if newNotes.Length = 0 then None
                            elif newNotes.Length = utt.Notes.Length then Some(KeyValuePair(utt, utt))
                            else Some(KeyValuePair(utt, utt.SetNotes newNotes)))
                        |> ImmutableDictionary.CreateRange
                    x.ProgramModel.ActiveComp |> Rp.set(
                        comp.SetUtts(ImmutableArray.CreateRange uttToNewDict.Values))
                    let activeUtt = selection.ActiveUtt |> Option.bind(uttToNewDict.TryGetValue >> Option.ofByRef)
                    x.ProgramModel.ActiveSelection |> Rp.set(CompSelection(activeUtt, ImmutableHashSet.Empty))

                    x.ProgramModel.UndoRedoStack.PushUndo(
                        DeleteNote, (comp, selection), (!!x.ProgramModel.ActiveComp, !!x.ProgramModel.ActiveSelection))

            | Key.Z when Keyboard.Modifiers.IsCtrl ->
                x.ProgramModel.Undo()

            | Key.Y when Keyboard.Modifiers.IsCtrl ->
                x.ProgramModel.Redo()

            | Key.Z when Keyboard.Modifiers = (ModifierKeys.Control ||| ModifierKeys.Shift) ->
                x.ProgramModel.Redo()

            | _ -> ()

