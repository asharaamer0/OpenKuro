// Run in a separate, empty After Effects instance. Never closes an existing project.
// File > Scripts > Run Script File. Does not save or overwrite an AE project.
(function () {
    if (app.project && app.project.numItems > 0) { alert("Use an empty After Effects instance for Kuro preset inspection."); return; }
    var scriptFolder = File($.fileName).parent;
    var root = scriptFolder.parent;
    var source = new Folder(root.fsName + "/True Motion");
    var out = new Folder(root.fsName + "/artifacts/preset-inspection"); out.create();
    function quote(s) { return '"' + String(s).replace(/\\/g, "\\\\").replace(/"/g, '\\"').replace(/\r/g, "\\r").replace(/\n/g, "\\n").replace(/\t/g, "\\t") + '"'; }
    function json(v) { if (v === null || v === undefined) return "null"; if (typeof v === "string") return quote(v); if (typeof v === "number" || typeof v === "boolean") return String(v); if (v instanceof Array) { var a=[]; for(var i=0;i<v.length;i++) a.push(json(v[i])); return "["+a.join(",")+"]"; } var a=[]; for(var k in v) if(v.hasOwnProperty(k)) a.push(quote(k)+":"+json(v[k])); return "{"+a.join(",")+"}"; }
    function safe(v) { if(typeof v==="number" || typeof v==="string" || typeof v==="boolean" || v instanceof Array) return v; return String(v); }
    function prop(p) {
        var o = {name:p.name,matchName:p.matchName};
        if(p.propertyType === PropertyType.PROPERTY) {
            try { o.value=safe(p.value); } catch(e){}
            o.keys=[];
            for(var k=1;k<=p.numKeys;k++) { var key={time:p.keyTime(k),value:safe(p.keyValue(k))}; try { key.inType=String(p.keyInInterpolationType(k)); key.outType=String(p.keyOutInterpolationType(k)); var ie=p.keyInTemporalEase(k),oe=p.keyOutTemporalEase(k); key.inEase=[];key.outEase=[];for(var j=0;j<ie.length;j++)key.inEase.push({speed:ie[j].speed,influence:ie[j].influence});for(var j=0;j<oe.length;j++)key.outEase.push({speed:oe[j].speed,influence:oe[j].influence}); }catch(e){} o.keys.push(key); }
            if(p.canSetExpression) { o.expression=p.expression; o.expressionEnabled=p.expressionEnabled; }
            o.samples=[]; for(var f=0;f<=90;f+=3) try{o.samples.push({frame:f,value:safe(p.valueAtTime(f/30,false))});}catch(e){}
        } else { o.children=[]; for(var i=1;i<=p.numProperties;i++) o.children.push(prop(p.property(i))); }
        return o;
    }
    app.beginSuppressDialogs();
    try {
        if(!app.project) app.newProject(); var files=source.getFiles("*.ffx");
        for(var i=0;i<files.length;i++) {
            var comp=app.project.items.addComp(files[i].displayName,1920,1080,1,4,30); var layer=comp.layers.addText("Kuro motion reference");
            var td=layer.property("Source Text").value;td.font="SegoeUI";td.fontSize=96;td.fillColor=[1,1,1];layer.property("Source Text").setValue(td);layer.property("Position").setValue([960,540]);
            comp.time=0;layer.selected=true;var report={file:files[i].displayName,composition:{width:1920,height:1080,fps:30,text:"Kuro motion reference",font:"SegoeUI",size:96}};
            try{layer.applyPreset(files[i]);report.properties=prop(layer);report.applied=true;}catch(e){report.error=String(e);}
            var output=new File(out.fsName+"/"+files[i].displayName+".json");output.encoding="UTF-8";output.open("w");output.write(json(report));output.close();layer.selected=false;
            // Comparison sequences should be rendered at every frame, using these exact compositions.
        }
        var done=new File(out.fsName+"/complete.txt");done.open("w");done.write("Inspected "+files.length+" presets. Property extraction only; no fidelity comparison claimed.");done.close();
    } finally { app.endSuppressDialogs(false); }
})();
