import tkinter as tk
from tkinter import filedialog, messagebox
import json
import math
import os

# Try to import Pillow for better image handling
try:
    from PIL import Image, ImageTk
    HAS_PILLOW = True
except ImportError:
    HAS_PILLOW = False

class BoardPosEditor:
    def __init__(self, root):
        self.root = root
        self.root.title("Parchís Board Position Editor")
        
        # Configuration
        self.image_path = "texture.png"
        self.canvas_width = 1000
        self.canvas_height = 1000
        self.center_x, self.center_y = 500, 500
        self.scale = 0.002 # Units per pixel (approx)
        self.symmetry_enabled = tk.BooleanVar(value=True)
        
        # Data
        self.markers = [] # List of {id, name, color, x, y, type}
        self.selected_marker = None
        
        # UI Setup
        self.setup_ui()
        self.init_markers()
        
        # Load Image
        self.load_image()
        
    def setup_ui(self):
        # Main Layout
        self.main_frame = tk.Frame(self.root)
        self.main_frame.pack(fill=tk.BOTH, expand=True)
        
        # Canvas
        self.canvas = tk.Canvas(self.main_frame, width=self.canvas_width, height=self.canvas_height, bg="gray")
        self.canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        
        # Controls
        self.control_frame = tk.Frame(self.main_frame, width=300)
        self.control_frame.pack(side=tk.RIGHT, fill=tk.Y, padx=10, pady=10)
        
        tk.Label(self.control_frame, text="Board Editor Tools", font=("Arial", 14, "bold")).pack(pady=10)
        
        tk.Checkbutton(self.control_frame, text="4-Fold Symmetry (Sync)", variable=self.symmetry_enabled).pack(anchor=tk.W)
        
        tk.Button(self.control_frame, text="Export to Godot (.tscn format)", command=self.export_to_godot).pack(fill=tk.X, pady=5)
        tk.Button(self.control_frame, text="Export to JSON", command=self.export_to_json).pack(fill=tk.X, pady=5)
        
        self.info_label = tk.Label(self.control_frame, text="Select a marker to edit", justify=tk.LEFT)
        self.info_label.pack(pady=20, anchor=tk.W)
        
        tk.Label(self.control_frame, text="Project Scale (Units per pixel):").pack(anchor=tk.W)
        self.scale_entry = tk.Entry(self.control_frame)
        self.scale_entry.insert(0, str(self.scale))
        self.scale_entry.pack(fill=tk.X)
        
        # Events
        self.canvas.bind("<ButtonPress-1>", self.on_click)
        self.canvas.bind("<B1-Motion>", self.on_drag)
        self.canvas.bind("<ButtonRelease-1>", self.on_release)
        
    def init_markers(self):
        # Master Arm: Yellow (South, Angle 0)
        # Ranks 1 to 8: Ring + Corridor
        
        # 1. Ring Positions
        # Outbound Right (1-8)
        for i in range(1, 9):
            self.add_marker_set(f"Pos_{i:02d}", 45, 380 - (i-1)*35, "outbound", i, rank=i)
            
        # Inbound Left (67-60)
        for i in range(60, 68):
            rank = (67 - i) + 1
            self.add_marker_set(f"Pos_{i:02d}", -45, 380 - (rank-1)*35, "inbound", i, rank=rank)
            
        # Tips (Rank 0)
        self.add_marker_set("Pos_68", 0, 420, "tip", 68, rank=0)
        
        # 2. Home Corridors (7 steps - reduced from 8 based on user feedback)
        for i in range(1, 8):
            # Rank 1 was reported as extra/overlapping entrance, 
            # so we start corridor squares from rank 2.
            self.add_marker_set(f"HomeYellow_{i}", 0, 346 - (i-1)*34, "home", i, rank=i+1)
            
        # 3. Bases (Rank -1)
        for i in range(4):
            bx = 220 + (40 if i%2==0 else -40)
            by = 280 + (40 if i//2==0 else -40)
            self.add_marker_set(f"BaseYellow_{i}", bx, by, "base", i, rank=-1)
            
        # 4. Goals (Rank 10)
        self.add_marker_set("GoalYellow", 0, 50, "goal", 0, rank=10)

    def add_marker_set(self, base_name, rx, ry, mtype, step_idx, rank):
        cx, cy = self.center_x, self.center_y
        configs = [(0, "Yellow"), (90, "Blue"), (180, "Red"), (270, "Green")]
        set_id = f"set_{base_name}"
        
        for angle, color in configs:
            rad = math.radians(angle)
            x = cx + rx * math.cos(rad) - ry * math.sin(rad)
            y = cy + rx * math.sin(rad) + ry * math.cos(rad)
            final_name = self.get_mapped_name(base_name, color, step_idx, mtype)
            # Store lane and rank for sync logic
            m = {"name": final_name, "x": x, "y": y, "color": color.lower(), 
                 "type": mtype, "set_id": set_id, "angle": angle, "rank": rank, "lane": mtype}
            self.markers.append(m)

    def get_mapped_name(self, base_name, color, step_idx, mtype):
        # User requested remapping:
        # Blue -> Yellow, Green -> Red, Red -> Green, Yellow -> Blue
        color_map = {
            "Blue": "Yellow",
            "Green": "Red",
            "Red": "Green",
            "Yellow": "Blue"
        }
        name_color = color_map.get(color, color)
        
        if mtype == "home":
            return f"Home{name_color}_{step_idx}"
        if mtype == "base":
            return f"Base{name_color}_{step_idx}"
        
        # Ring mapping (Standard 68) - uses the PHYSICAL arm (unmapped 'color') 
        # to determine its offset on the board ring.
        if mtype == "outbound" or mtype == "inbound":
            offsets = {"Yellow": 0, "Blue": 17, "Red": 34, "Green": 51}
            off = offsets[color]
            
            if step_idx <= 8: # Outbound
                return f"Pos_{(step_idx + off) % 68 or 68:02d}"
            else: # Inbound (60-67)
                rel_step = 67 - step_idx # 0 to 7
                base_inbound = {"Yellow": 67, "Blue": 16, "Red": 33, "Green": 50}
                return f"Pos_{base_inbound[color] - rel_step:02d}"
        
        if mtype == "tip":
            # Map the tip name to the player color
            if name_color == "Blue": return "Pos_68"
            if name_color == "Yellow": return "Pos_17"
            if name_color == "Green": return "Pos_34"
            if name_color == "Red": return "Pos_51"
            
        if mtype == "goal":
            return f"Goal{name_color}"
            
        return base_name

    def load_image(self):
        if not os.path.exists(self.image_path):
            messagebox.showwarning("Warning", f"Image {self.image_path} not found. Using blank canvas.")
            return
            
        if HAS_PILLOW:
            img = Image.open(self.image_path)
            # Use fixed dimensions for consistency with coordinate system
            img = img.resize((self.canvas_width, self.canvas_height), Image.Resampling.LANCZOS)
            self.tk_img = ImageTk.PhotoImage(img)
            self.canvas.create_image(0, 0, anchor=tk.NW, image=self.tk_img)
        else:
            try:
                self.tk_img = tk.PhotoImage(file=self.image_path)
                # If canvas dimensions are not yet available, use a default or original image size
                self.canvas.create_image(0, 0, anchor=tk.NW, image=self.tk_img)
            except:
                messagebox.showerror("Error", "Could not load image without Pillow and default loader failed.")
        self.draw_markers()

    def draw_markers(self):
        self.canvas.delete("marker")
        for m in self.markers:
            r = 5
            fill = "lime" if m["color"] == "yellow" else "black"
            self.canvas.create_oval(m["x"]-r, m["y"]-r, m["x"]+r, m["y"]+r, 
                                   fill=fill, outline="white", tags=("marker", m["name"], m["color"]))

    def on_click(self, event):
        item = self.canvas.find_closest(event.x, event.y)
        tags = self.canvas.gettags(item)
        if "marker" in tags and "yellow" in tags:
            name = tags[1]
            for m in self.markers:
                if m["name"] == name:
                    self.selected_marker = m
                    self.info_label.config(text=f"Editing Rank {m['rank']}\n(Global Alignment)")
                    break

    def on_drag(self, event):
        if self.selected_marker:
            cx, cy = self.center_x, self.center_y
            # Yellow Master arm (angle=0) -> rx is x-cx, ry is y-cy
            new_rx = event.x - cx
            new_ry = event.y - cy
            
            rank = self.selected_marker.get("rank")
            lane = self.selected_marker.get("lane")

            if rank == -1: # Bases: Individual drag in Master arm, then sync slaves
                self.selected_marker["x"] = event.x
                self.selected_marker["y"] = event.y
                self.sync_symmetry(self.selected_marker)
            else:
                # 1. Lane Sync (Lateral): Move the entire lane horizontally
                for m in self.markers:
                    if m["color"] == "yellow" and m["lane"] == lane:
                        m["x"] = cx + new_rx
                        self.sync_symmetry(m)
                
                # 2. Rank Sync (Longitudinal): Move the entire rank vertically
                for m in self.markers:
                    if m["color"] == "yellow" and m["rank"] == rank:
                        m["y"] = cy + new_ry
                        self.sync_symmetry(m)
            
            self.draw_markers()

    def sync_symmetry(self, master_m):
        cx, cy = self.center_x, self.center_y
        # Master (Yellow) rx/ry
        rx = master_m["x"] - cx
        ry = master_m["y"] - cy
        
        set_id = master_m.get("set_id")
        for slave in self.markers:
            if slave.get("set_id") == set_id and slave["color"] != "yellow":
                rad = math.radians(slave["angle"])
                slave["x"] = cx + rx * math.cos(rad) - ry * math.sin(rad)
                slave["y"] = cy + rx * math.sin(rad) + ry * math.cos(rad)

    def on_release(self, event):
        self.selected_marker = None

    def get_world_coords(self, mx, my):
        try:
            scale = float(self.scale_entry.get())
        except ValueError: # Catch ValueError if entry is not a valid float
            scale = self.scale # Fallback to default scale
        wx = (mx - self.center_x) * scale
        wz = (my - self.center_y) * scale
        return wx, wz

    def get_node_string(self, marker, scale):
        wx, wz = self.get_world_coords(marker["x"], marker["y"])
        # Godot uses Y as up, Z as depth. Our Y is depth.
        return (f'[node name="{marker["name"]}" type="Marker3D" parent="."]\n'
                f'transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, {wx:.4f}, 0, {wz:.4f})')

    def export_to_godot(self):
        lines = []
        try:
            scale = float(self.scale_entry.get())
        except ValueError:
            scale = 0.772 # Godot units per 400px nominal radius
        y_offset = 0.011436
        
        lines.append('[node name="Positions" type="Node3D" parent="." unique_id="200000010"]')
        lines.append(f'transform = Transform3D({scale}, 0, 0, 0, {scale}, 0, 0, 0, {scale}, 0, {y_offset}, 0)')
        lines.append('')
        for m in sorted(self.markers, key=lambda x: x["name"]):
            if m["type"] in ["outbound", "inbound", "tip"]:
                node_line = self.get_node_string(m, scale)
                lines.append(node_line)
                lines.append('')

        lines.append('[node name="HomePositions" type="Node3D" parent="." unique_id="200000020"]')
        lines.append(f'transform = Transform3D({scale}, 0, 0, 0, {scale}, 0, 0, 0, {scale}, 0, {y_offset}, 0)')
        lines.append('')
        for m in sorted(self.markers, key=lambda x: x["name"]):
            if m["type"] == "home":
                node_line = self.get_node_string(m, scale)
                lines.append(node_line)
                lines.append('')

        lines.append('[node name="BasePositions" type="Node3D" parent="." unique_id="200000030"]')
        lines.append(f'transform = Transform3D({scale}, 0, 0, 0, {scale}, 0, 0, 0, {scale}, 0, {y_offset}, 0)')
        lines.append('')
        for m in sorted(self.markers, key=lambda x: x["name"]):
            if m["type"] == "base" or m["type"] == "goal":
                node_line = self.get_node_string(m, scale)
                lines.append(node_line)
                lines.append('')
        
        with open("exported_markers.tscn_snippet", "w") as f:
            f.write("\n".join(lines))
        messagebox.showinfo("Export Success", "Saved to exported_markers.tscn_snippet")

    def export_to_json(self):
        data = []
        for m in self.markers:
            wx, wz = self.get_world_coords(m["x"], m["y"])
            data.append({"name": m["name"], "world_x": wx, "world_z": wz})
        with open("board_positions.json", "w") as f:
            json.dump(data, f, indent=4)
        messagebox.showinfo("Export Success", "Saved to board_positions.json")

if __name__ == "__main__":
    root = tk.Tk()
    app = BoardPosEditor(root)
    root.mainloop()
