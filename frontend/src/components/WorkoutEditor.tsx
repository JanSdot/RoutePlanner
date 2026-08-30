import { useEffect, useState } from "react";
import type { EditorZone, WorkoutBlockSpec } from "../types";

const ZONES: EditorZone[] = ["GA1", "GA2", "EB", "SB", "VO2max"];

interface InnerStep {
  id: string;
  zone: EditorZone;
  durationMinutes: number;
}

interface StepBlock {
  id: string;
  kind: "step";
  zone: EditorZone;
  durationMinutes: number;
}

interface RepeatBlock {
  id: string;
  kind: "repeat";
  times: number;
  steps: InnerStep[];
}

type EditorBlock = StepBlock | RepeatBlock;

let nextId = 1;
function newId(): string {
  return String(nextId++);
}

function newInnerStep(): InnerStep {
  return { id: newId(), zone: "EB", durationMinutes: 5 };
}

function toSpec(blocks: EditorBlock[]): WorkoutBlockSpec[] {
  return blocks.map((b) =>
    b.kind === "step"
      ? { step: { zone: b.zone, durationMinutes: b.durationMinutes } }
      : {
          repeatTimes: b.times,
          repeatSteps: b.steps.map((s) => ({ zone: s.zone, durationMinutes: s.durationMinutes })),
        },
  );
}

interface WorkoutEditorProps {
  onChange: (blocks: WorkoutBlockSpec[]) => void;
}

export function WorkoutEditor({ onChange }: WorkoutEditorProps) {
  const [blocks, setBlocks] = useState<EditorBlock[]>([
    { id: newId(), kind: "step", zone: "GA1", durationMinutes: 20 },
  ]);

  useEffect(() => {
    onChange(toSpec(blocks));
    // onChange kommt aus dem Elternteil als neue Funktionsinstanz pro Render - nur auf
    // Aenderungen an blocks selbst reagieren, sonst Endlosschleife.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [blocks]);

  function addStep() {
    setBlocks((prev) => [...prev, { id: newId(), kind: "step", zone: "GA1", durationMinutes: 5 }]);
  }

  function addRepeat() {
    setBlocks((prev) => [...prev, { id: newId(), kind: "repeat", times: 3, steps: [newInnerStep()] }]);
  }

  function removeBlock(id: string) {
    setBlocks((prev) => prev.filter((b) => b.id !== id));
  }

  function updateStep(id: string, patch: Partial<Pick<StepBlock, "zone" | "durationMinutes">>) {
    setBlocks((prev) =>
      prev.map((b) => (b.id === id && b.kind === "step" ? { ...b, ...patch } : b)),
    );
  }

  function updateRepeatTimes(id: string, times: number) {
    setBlocks((prev) => prev.map((b) => (b.id === id && b.kind === "repeat" ? { ...b, times } : b)));
  }

  function addInnerStep(repeatId: string) {
    setBlocks((prev) =>
      prev.map((b) => (b.id === repeatId && b.kind === "repeat" ? { ...b, steps: [...b.steps, newInnerStep()] } : b)),
    );
  }

  function removeInnerStep(repeatId: string, innerId: string) {
    setBlocks((prev) =>
      prev.map((b) =>
        b.id === repeatId && b.kind === "repeat" ? { ...b, steps: b.steps.filter((s) => s.id !== innerId) } : b,
      ),
    );
  }

  function updateInnerStep(repeatId: string, innerId: string, patch: Partial<Pick<InnerStep, "zone" | "durationMinutes">>) {
    setBlocks((prev) =>
      prev.map((b) =>
        b.id === repeatId && b.kind === "repeat"
          ? { ...b, steps: b.steps.map((s) => (s.id === innerId ? { ...s, ...patch } : s)) }
          : b,
      ),
    );
  }

  return (
    <div className="workout-editor">
      {blocks.map((block) =>
        block.kind === "step" ? (
          <div className="editor-block" key={block.id}>
            <select
              value={block.zone}
              onChange={(e) => updateStep(block.id, { zone: e.target.value as EditorZone })}
            >
              {ZONES.map((z) => (
                <option key={z} value={z}>
                  {z}
                </option>
              ))}
            </select>
            <input
              type="number"
              min={1}
              value={block.durationMinutes}
              onChange={(e) => updateStep(block.id, { durationMinutes: Number(e.target.value) })}
            />
            <span className="editor-unit">min</span>
            <button type="button" className="editor-remove" onClick={() => removeBlock(block.id)}>
              ✕
            </button>
          </div>
        ) : (
          <div className="editor-block editor-repeat" key={block.id}>
            <div className="editor-repeat-header">
              <span>Wiederholung ×</span>
              <input
                type="number"
                min={1}
                value={block.times}
                onChange={(e) => updateRepeatTimes(block.id, Number(e.target.value))}
              />
              <button type="button" className="editor-remove" onClick={() => removeBlock(block.id)}>
                ✕
              </button>
            </div>
            {block.steps.map((step) => (
              <div className="editor-block editor-inner-step" key={step.id}>
                <select
                  value={step.zone}
                  onChange={(e) => updateInnerStep(block.id, step.id, { zone: e.target.value as EditorZone })}
                >
                  {ZONES.map((z) => (
                    <option key={z} value={z}>
                      {z}
                    </option>
                  ))}
                </select>
                <input
                  type="number"
                  min={1}
                  value={step.durationMinutes}
                  onChange={(e) => updateInnerStep(block.id, step.id, { durationMinutes: Number(e.target.value) })}
                />
                <span className="editor-unit">min</span>
                <button
                  type="button"
                  className="editor-remove"
                  onClick={() => removeInnerStep(block.id, step.id)}
                >
                  ✕
                </button>
              </div>
            ))}
            <button type="button" className="editor-add-inner" onClick={() => addInnerStep(block.id)}>
              + Schritt in Wiederholung
            </button>
          </div>
        ),
      )}

      <div className="editor-add-row">
        <button type="button" onClick={addStep}>
          + Schritt
        </button>
        <button type="button" onClick={addRepeat}>
          + Wiederholung
        </button>
      </div>
    </div>
  );
}
