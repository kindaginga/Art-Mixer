package {
    import flash.display.MovieClip;
    import flash.display.Sprite;
    import flash.events.MouseEvent;
    import flash.text.TextField;
    import flash.text.TextFieldAutoSize;
    import flash.text.TextFormat;

    // A small drop-up list in the bottom-right corner of the main menu that picks the main menu video pack.
    public class GShufPicker extends MovieClip {
        public var BGSCodeObj:Object;

        private const WIDTH:Number = 300;
        private const ROW:Number = 26;
        private const MARGIN:Number = 24;
        private const STAGE_W:Number = 1280;
        private const STAGE_H:Number = 720;

        private var names:Array = [];
        private var selected:int = 0;
        private var listOpen:Boolean = false;
        private var button:Sprite;
        private var buttonLabel:TextField;
        private var list:Sprite;
        private var caption:TextField;

        public function GShufPicker() {
            build();
        }

        // Called from the plugin with the pack names and the selected index.
        public function SetOptions(options:Array, selectedIndex:int):void {
            names = options;
            selected = selectedIndex;
            listOpen = false;
            redraw();
        }

        private function build():void {
            caption = makeText("Main menu art", 14, 0xB6B6B6);
            caption.x = STAGE_W - WIDTH - MARGIN;
            caption.y = STAGE_H - MARGIN - ROW - 20;
            addChild(caption);

            button = new Sprite();
            button.x = STAGE_W - WIDTH - MARGIN;
            button.y = STAGE_H - MARGIN - ROW;
            button.buttonMode = true;
            button.mouseChildren = false;
            button.addEventListener(MouseEvent.CLICK, onButtonClick);
            buttonLabel = makeText("", 16, 0xFFFFFF);
            buttonLabel.x = 8;
            buttonLabel.y = 3;
            button.addChild(buttonLabel);
            addChild(button);

            list = new Sprite();
            list.x = button.x;
            list.visible = false;
            addChild(list);
            redraw();
        }

        private function redraw():void {
            var current:String = names.length > 0 && selected >= 0 && selected < names.length ? String(names[selected]) : "Vanilla";
            buttonLabel.text = current + (names.length > 1 ? "   ▲" : "");
            drawBox(button, WIDTH, ROW, 0x1A1A1A, 0.85, 0x9DFF9D);

            while (list.numChildren > 0) list.removeChildAt(0);
            var rows:int = names.length;
            drawBox(list, WIDTH, ROW * rows, 0x101010, 0.92, 0x9DFF9D);
            list.y = button.y - ROW * rows;
            for (var i:int = 0; i < rows; i++) {
                var row:Sprite = new Sprite();
                row.y = i * ROW;
                row.buttonMode = true;
                row.mouseChildren = false;
                row.name = "row" + i;
                drawBox(row, WIDTH, ROW, i == selected ? 0x2E5A2E : 0x101010, i == selected ? 0.95 : 0.01, 0);
                var t:TextField = makeText(String(names[i]), 15, i == selected ? 0xFFFFFF : 0xD0D0D0);
                t.x = 8;
                t.y = 3;
                row.addChild(t);
                row.addEventListener(MouseEvent.CLICK, onRowClick);
                row.addEventListener(MouseEvent.ROLL_OVER, onRowOver);
                row.addEventListener(MouseEvent.ROLL_OUT, onRowOut);
                list.addChild(row);
            }
            list.visible = listOpen && rows > 0;
        }

        private function onButtonClick(e:MouseEvent):void {
            if (names.length < 2) return;
            listOpen = !listOpen;
            list.visible = listOpen;
        }

        private function onRowClick(e:MouseEvent):void {
            var index:int = int(String(e.currentTarget.name).substr(3));
            selected = index;
            listOpen = false;
            redraw();
            if (BGSCodeObj != null && BGSCodeObj.onSelect != null) BGSCodeObj.onSelect(index);
        }

        private function onRowOver(e:MouseEvent):void { e.currentTarget.alpha = 0.7; }
        private function onRowOut(e:MouseEvent):void { e.currentTarget.alpha = 1.0; }

        private function drawBox(target:Sprite, w:Number, h:Number, fill:uint, alpha:Number, line:uint):void {
            target.graphics.clear();
            if (line != 0) target.graphics.lineStyle(1, line, 0.8);
            target.graphics.beginFill(fill, alpha);
            target.graphics.drawRect(0, 0, w, h);
            target.graphics.endFill();
        }

        private function makeText(text:String, size:Number, color:uint):TextField {
            var t:TextField = new TextField();
            t.defaultTextFormat = new TextFormat("$MAIN_Font", size, color);
            t.autoSize = TextFieldAutoSize.LEFT;
            t.selectable = false;
            t.mouseEnabled = false;
            t.text = text;
            return t;
        }
    }
}
