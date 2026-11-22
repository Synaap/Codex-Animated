// Windows splash playback, adapted from panding999/codex-startup-animation.
(() => {
  'use strict';
  const $ = selector => document.querySelector(selector);
  const root = $('.window');
  const art = $('.artwork');
  const scene = $('.scene');
  const intro = $('.intro');
  const hud = $('.hud');
  const sweep = $('.light-sweep');
  const subtitle = $('.subtitle');
  const duration = 12000;
  const rate = Math.max(1, Math.min(4, parseInt(root.dataset.rate, 10) || 1));
  const style = root.dataset.style || 'full';
  const motionPreference = window.matchMedia('(prefers-reduced-motion: reduce)');
  const stillParam = parseFloat(new URLSearchParams(location.search).get('still'));
  const still = stillParam > 0 ? stillParam : 0;
  if (still) root.dataset.still = '1';
  // Alternative animation skins. Each is a self-contained {duration, prepare(),
  // render(t)} that injects its own nodes and hides the full-skin chrome via
  // [data-style] CSS; reduced motion falls back to the calm card for all of them.
  const SKINS = {
    terminal: {
      duration: 9000,
      prepare() {
        this.lines = Array.from(document.querySelectorAll('.boot-logs div')).map(d => d.textContent);
        this.lines.push('> ' + $('.boot-title').textContent);
        const term = document.createElement('pre');
        term.className = 'term';
        term.innerHTML = this.lines.map(() => '<div class="tl"></div>').join('') +
          '<div class="tbar"></div><div class="tready"></div><i class="cursor"></i>';
        scene.appendChild(term);
        this.term = term;
      },
      render(t) {
        const s = t / 1000;
        this.term.querySelectorAll('.tl').forEach((el, i) => {
          const start = .3 + i * .62, typed = Math.floor(Math.max(0, s - start) / .018);
          const text = this.lines[i] || '';
          el.textContent = typed <= 0 ? '' : text.slice(0, Math.min(text.length, typed));
        });
        const fill = Math.round(clamp((s - 4.6) / 4) * 14);
        this.term.querySelector('.tbar').textContent =
          `> [${'='.repeat(fill)}${fill < 14 ? '>' : ''}${' '.repeat(Math.max(0, 14 - fill - 1))}] ${(clamp((s - 4.6) / 4) * 100).toFixed(0).padStart(3)}%`;
        this.term.querySelector('.tready').textContent = s >= 8.1 ? '> READY' : '';
        art.style.opacity = String(.5 * smooth((s - 4.4) / 3));
        scene.style.opacity = String(1 - smooth((s - 8.4) / .6));
        root.dataset.elapsed = s.toFixed(2);
        root.dataset.stage = 'terminal';
      }
    },
    manga: {
      duration: 8000,
      prepare() {
        const panel = document.createElement('div');
        panel.className = 'manga-panel';
        panel.appendChild(art);
        const dots = document.createElement('div');
        dots.className = 'halftone';
        panel.appendChild(dots);
        scene.appendChild(panel);
        const lines = document.createElement('div');
        lines.className = 'speedlines';
        scene.appendChild(lines);
        const stamp = document.createElement('div');
        stamp.className = 'manga-title';
        stamp.textContent = $('.boot-title').textContent;
        scene.appendChild(stamp);
        this.panel = panel; this.lines = lines; this.stamp = stamp;
      },
      render(t) {
        const s = t / 1000;
        const k = smooth((s - .5) / .8);
        const overshoot = 1 + .1 * Math.sin(clamp((s - 1.3) / .5) * Math.PI);
        art.style.opacity = String(smooth((s - .7) / 1.1));
        this.panel.style.opacity = String(Math.min(1, k * 1.6));
        this.panel.style.transform = `translate(-50%,-50%) rotate(-2.5deg) scale(${(0.6 + 0.4 * k) * overshoot})`;
        this.lines.style.opacity = String(smooth((s - .95) / .08) * (1 - smooth((s - 1.45) / .5)) * .85);
        const st = clamp((s - 1.7) / .16);
        this.stamp.style.opacity = String(Math.min(1, st * 3));
        this.stamp.style.transform = `translate(-50%,-50%) rotate(-4deg) scale(${2.4 - 1.4 * st})`;
        scene.style.opacity = String(1 - smooth((s - 7.2) / .8));
        root.dataset.elapsed = s.toFixed(2);
        root.dataset.stage = 'manga';
      }
    },
    minimal: {
      duration: 7000,
      prepare() {
        const title = document.createElement('div');
        title.className = 'min-title';
        title.textContent = $('.boot-title').textContent;
        const sub = document.createElement('div');
        sub.className = 'min-sub';
        sub.textContent = $('#subtitle').textContent;
        const track = document.createElement('div');
        track.className = 'min-track';
        track.innerHTML = '<div class="min-fill"></div>';
        scene.append(title, sub, track);
        this.title = title; this.sub = sub; this.fill = track.firstChild;
      },
      render(t) {
        const s = t / 1000;
        art.style.opacity = String(.42 * smooth((s - 1) / 2));
        $('.shade').style.opacity = '.72';
        this.title.style.opacity = String(smooth((s - 1.2) / .9));
        this.title.style.letterSpacing = `${.55 - .32 * smooth((s - 1.2) / 1.6)}em`;
        this.sub.style.opacity = String(smooth((s - 2.4) / .6) * (1 - smooth((s - 5.4) / .8)));
        this.fill.style.transform = `scaleX(${clamp((s - 1.8) / 4.4)})`;
        scene.style.opacity = String(1 - smooth((s - 6.3) / .7));
        root.dataset.elapsed = s.toFixed(2);
        root.dataset.stage = 'minimal';
      }
    }
  };
  const reduced = !still && motionPreference.matches;
  // Reduced motion plays the calm card instead of skipping the animation; the
  // "short" style is the same card with a gentle fade.
  const skin = !reduced && style !== 'full' && style !== 'short' ? SKINS[style] : null;
  const card = reduced || style === 'short';
  const cardDuration = reduced ? 2400 : 3000;
  if (card) root.dataset.motion = reduced ? 'calm' : 'short';
  let completed = false;
  let frame = 0;
  let origin = 0;
  let lastUI = -Infinity;

  function finish(status) {
    if (completed) return;
    completed = true;
    cancelAnimationFrame(frame);
    root.dataset.completed = 'true';
    window.chrome?.webview?.postMessage(status);
    if (window.parent !== window) window.parent.postMessage({ splash: status }, location.origin);
  }

  $('.skip').addEventListener('click', () => finish('complete'));
  root.addEventListener('click', () => { if (!still) finish('complete'); });
  window.addEventListener('keydown', event => {
    if (event.key === 'Escape') {
      event.preventDefault();
      finish('complete');
    }
  });
  motionPreference.addEventListener('change', event => {
    if (event.matches && !still) finish('complete');
  });

  document.querySelectorAll('.boot-logs div').forEach(line => { if (!line.textContent.trim()) line.remove(); });
  if (!$('#subtitle').textContent.trim()) subtitle.style.display = 'none';
  let lineColor = '#c8c0cd';
  function prepareLineColor() {
    const m = /^#([0-9a-f]{6})$/i.exec(getComputedStyle(root).getPropertyValue('--accent').trim());
    if (!m) return;
    const accent = [0, 2, 4].map(i => parseInt(m[1].substr(i, 2), 16)), base = [0xd6, 0xd0, 0xdc];
    lineColor = `rgb(${base.map((b, i) => Math.round(b * .7 + accent[i] * .3)).join(',')})`;
  }
  function fitTitle() {
    const title = $('.boot-title'), box = $('.identity');
    title.style.fontSize = '';
    const base = parseFloat(getComputedStyle(title).fontSize);
    if (title.scrollWidth > box.clientWidth)
      title.style.fontSize = `${Math.max(18, base * box.clientWidth / title.scrollWidth)}px`;
  }
  const clamp = n => Math.max(0, Math.min(1, n));
  const smooth = n => { const t = clamp(n); return t*t*(3-2*t); };
  const particles = Array.from({length: reduced ? 0 : 14}, (_, i) => {
    const node = document.createElement('i'); node.className = 'particle';
    const size = i % 4 === 0 ? 3 : 1.5;
    Object.assign(node.style, {width:`${size}px`,height:`${size}px`,left:`${(i*37+7)%100}%`,top:`${(i*23+17)%100}%`});
    $('.particles').appendChild(node); return node;
  });
  const milestones = [[0,0],[.12,.06],[.35,.41],[.56,.76],[.66,.924],[.72,.97],[.77,1],[1,1]];
  function progress(t) {
    const p = t / duration;
    for (let i=1;i<milestones.length;i++) {
      if (p <= milestones[i][0]) {
        const [a,x] = milestones[i-1], [b,y] = milestones[i];
        return x+(y-x)*smooth((p-a)/(b-a));
      }
    }
    return 1;
  }
  // Split the selected image's own contours once; no particles are allocated per frame.
  const canvas = $('.line-art'), ctx = canvas.getContext('2d');
  function preparePaths(raw) { return raw.map(points => {
    let total=0;
    for(let i=1;i<points.length;i++)total+=Math.hypot(points[i][0]-points[i-1][0],points[i][1]-points[i-1][1]);
    const full=new Path2D();points.forEach((point,i)=>i?full.lineTo(...point):full.moveTo(...point));
    return {points,total,full};
  }); }
  function prepareFragments(paths){
    const result=[],span=Math.max(18,paths.reduce((sum,path)=>sum+path.total,0)/1400);
    const noise=n=>{const x=Math.sin(n*127.1+311.7)*43758.5453;return x-Math.floor(x);};
    function add(points){
      if(points.length<2)return;
      const first=points[0],last=points[points.length-1],x=(first[0]+last[0])/2,y=(first[1]+last[1])/2;
      const seed=result.length+1;
      result.push({x,y,points:points.map(point=>[point[0]-x,point[1]-y]),
        sx:(x+(noise(seed)-.5)*440+1536)%1536,sy:(y+(noise(seed+271)-.5)*300+1024)%1024,
        angle:(noise(seed+563)-.5)*Math.PI*2,seed:noise(seed+811)*Math.PI*2});
    }
    for(const path of paths){
      if(path.points.length<2)continue;
      let chunk=[path.points[0]],used=0;
      for(let i=1;i<path.points.length;i++){
        let a=path.points[i-1];const b=path.points[i];
        let length=Math.hypot(b[0]-a[0],b[1]-a[1]);
        while(length>0&&used+length>=span){
          const ratio=(span-used)/length,cut=[a[0]+(b[0]-a[0])*ratio,a[1]+(b[1]-a[1])*ratio];
          chunk.push(cut);add(chunk);chunk=[cut];a=cut;used=0;
          length=Math.hypot(b[0]-a[0],b[1]-a[1]);
        }
        if(length>0){chunk.push(b);used+=length;}
      }
      add(chunk);
    }
    return result;
  }
  let paths, fragments;
  let identityTexture=null,identityPieces=[];
  function releaseIdentityTexture(){
    if(identityTexture)identityTexture.width=identityTexture.height=0;
    identityTexture=null;identityPieces=[];
  }
  function prepareIdentityTexture(){
    releaseIdentityTexture();
    const bounds=canvas.getBoundingClientRect(),scale=Math.max(bounds.width/1536,bounds.height/1024);
    if(!scale)return;
    const ox=bounds.left+(bounds.width-1536*scale)/2,oy=bounds.top+(bounds.height-1024*scale)/2;
    const rect=node=>{const r=node.getBoundingClientRect();return {x:(r.left-ox)/scale,y:(r.top-oy)/scale,w:r.width/scale,h:r.height/scale};};
    const texture=document.createElement('canvas');texture.width=1536;texture.height=1024;
    const ink=texture.getContext('2d');ink.imageSmoothingEnabled=false;
    const avatar=rect($('.avatar')),radar=rect($('.radar')),border=rect($('.avatar-frame'));
    const cx=radar.x+radar.w/2,cy=radar.y+radar.h/2;
    ink.strokeStyle='#a397a0';ink.lineWidth=1.3;
    ink.beginPath();ink.arc(cx,cy,radar.w*.4425,0,Math.PI*2);ink.stroke();
    ink.setLineDash([9,12]);ink.globalAlpha=.4;
    ink.beginPath();ink.arc(cx,cy,radar.w*.3775,0,Math.PI*2);ink.stroke();ink.setLineDash([]);ink.globalAlpha=.8;
    for(let i=0;i<60;i++){
      const a=i*Math.PI/30,r0=radar.w*(i%5===0?.4525:.46),r1=radar.w*(i%5===0?.4975:.48);
      ink.beginPath();ink.moveTo(cx+Math.sin(a)*r0,cy-Math.cos(a)*r0);ink.lineTo(cx+Math.sin(a)*r1,cy-Math.cos(a)*r1);ink.stroke();
    }
    ink.globalAlpha=1;ink.strokeRect(border.x,border.y,border.w,border.h);
    const portrait = $('.avatar');
    const zoom = Math.max(avatar.w / portrait.naturalWidth, avatar.h / portrait.naturalHeight);
    const sw = avatar.w / zoom, sh = avatar.h / zoom;
    ink.drawImage(portrait, (portrait.naturalWidth - sw) / 2, (portrait.naturalHeight - sh) / 2,
      sw, sh, avatar.x, avatar.y, avatar.w, avatar.h);
    // Neutralize the bitmap once, not with an expensive live blur/filter on every frame.
    ink.globalCompositeOperation='saturation';ink.fillStyle='#999';
    ink.fillRect(avatar.x,avatar.y,avatar.w,avatar.h);ink.globalCompositeOperation='source-over';
    const textBounds=[];
    for(const selector of ['.boot-title','.boot-caption']){
      const node=$(selector),r=rect(node),style=window.getComputedStyle(node);
      const text=style.textTransform==='uppercase'?node.textContent.toUpperCase():node.textContent;
      if(node.textContent)textBounds.push(r);
      ink.font=`${style.fontWeight} ${parseFloat(style.fontSize)/scale}px ${style.fontFamily}`;
      ink.textAlign='center';ink.textBaseline='middle';ink.fillStyle=style.color;
      ink.globalAlpha=1;
      ink.letterSpacing=`${(parseFloat(style.letterSpacing)||0)/scale}px`;
      ink.fillText(text,r.x+r.w/2,r.y+r.h/2,r.w);
    }
    ink.globalAlpha=1;
    ink.globalCompositeOperation='source-atop';ink.globalAlpha=.24;ink.fillStyle='#9b9099';
    ink.fillRect(0,0,1536,1024);ink.globalAlpha=1;ink.globalCompositeOperation='source-over';
    // Geometric tile selection works for file:// images too, without reading protected pixels.
    const tile=36,boxes=[avatar,border,...textBounds];
    const overlaps=(x,y,r)=>x<r.x+r.w&&x+tile>r.x&&y<r.y+r.h&&y+tile>r.y;
    for(let y=0;y<1024;y+=tile)for(let x=0;x<1536;x+=tile){
      const w=Math.min(tile,1536-x),h=Math.min(tile,1024-y);
      const distance=Math.hypot(x+w/2-cx,y+h/2-cy);
      const onRing=[.3775,.4425,.48].some(radius=>Math.abs(distance-radar.w*radius)<tile);
      if(onRing||boxes.some(box=>overlaps(x,y,box)))identityPieces.push({x,y,w,h,target:fragments[(identityPieces.length*37)%fragments.length]});
    }
    identityTexture=texture;
    canvas.dataset.identityPieces=String(identityPieces.length);
    fragments.forEach((fragment,i)=>{
      const tile=identityPieces[i%identityPieces.length];
      fragment.ix=tile?tile.x+tile.w/2:cx;fragment.iy=tile?tile.y+tile.h/2:cy;
    });
    for(const tile of identityPieces){tile.target.ix=tile.x+tile.w/2;tile.target.iy=tile.y+tile.h/2;}
  }
  function drawIdentityPieces(spread){
    if(!identityTexture||spread>=1)return;
    const fade=1-smooth((spread-.03)/.43),size=1-.9*spread;
    if(fade<=0)return;
    ctx.globalAlpha=fade;ctx.imageSmoothingEnabled=false;
    for(const tile of identityPieces){
      const target=tile.target;
      const x=(tile.x+tile.w/2)*(1-spread)+target.sx*spread,y=(tile.y+tile.h/2)*(1-spread)+target.sy*spread;
      const angle=target.angle*spread,cs=Math.cos(angle)*size,sn=Math.sin(angle)*size;
      ctx.setTransform(cs,sn,-sn,cs,x,y);
      ctx.drawImage(identityTexture,tile.x,tile.y,tile.w,tile.h,-tile.w/2,-tile.h/2,tile.w,tile.h);
    }
    ctx.setTransform(1,0,0,1,0,0);ctx.globalAlpha=1;
  }
  let lastTrace=-1;
  function trace(p,drift=0,spread=1) {
    const stamp=p===1?1:p+drift*.00001;
    if(stamp===lastTrace)return;lastTrace=stamp;
    ctx.clearRect(0,0,1536,1024);
    ctx.strokeStyle=lineColor;ctx.lineWidth=1.25;ctx.lineCap='round';ctx.lineJoin='round';
    if(p===1){
      ctx.globalAlpha=1;paths.forEach(path=>ctx.stroke(path.full));
    }else{
      drawIdentityPieces(spread);
      if(spread>.04){
      ctx.globalAlpha=.82*smooth((spread-.04)/.36);ctx.beginPath();
      // Arrival time follows target X, so the left edge resolves before the face and right edge.
      const front=p*1.5-.25;
      for(const fragment of fragments){
        const arrival=smooth((front-fragment.x/1536+.16)/.32),loose=1-arrival;
        const angle=fragment.angle*loose,cs=Math.cos(angle),sn=Math.sin(angle);
        const sx=(fragment.ix??fragment.sx)*(1-spread)+fragment.sx*spread;
        const sy=(fragment.iy??fragment.sy)*(1-spread)+fragment.sy*spread;
        const x=sx*loose+fragment.x*arrival+Math.sin(drift*.8+fragment.seed)*7*loose*spread;
        const y=sy*loose+fragment.y*arrival+Math.cos(drift*.6+fragment.seed)*7*loose*spread;
        const size=.16+.84*arrival;
        fragment.points.forEach((point,i)=>{
          const px=x+(point[0]*cs-point[1]*sn)*size,py=y+(point[0]*sn+point[1]*cs)*size;
          if(i)ctx.lineTo(px,py);else ctx.moveTo(px,py);
        });
      }
      ctx.stroke();ctx.globalAlpha=1;
      }
    }
    canvas.dataset.progress=p.toFixed(3);canvas.dataset.fragments=String(fragments.length);
  }
  for(let i=0;i<60;i++){
    const tick=document.createElementNS('http://www.w3.org/2000/svg','line');
    const angle=i*Math.PI/30,inner=i%5===0?181:184;
    tick.setAttribute('x1',200+Math.sin(angle)*inner);tick.setAttribute('y1',200-Math.cos(angle)*inner);
    tick.setAttribute('x2',200+Math.sin(angle)*(i%5===0?199:192));tick.setAttribute('y2',200-Math.cos(angle)*(i%5===0?199:192));
    tick.style.opacity=i%5===0?'.9':'.55';$('.ticks').appendChild(tick);
  }
  function render(t) {
    const s=t/1000, p=progress(t), color=smooth((s-7.18)/.24), collapse=smooth((s-11)/.3), pinch=smooth((s-11.65)/.35);
    const dissolve=smooth((s-4.18)/.56);
    const closing=s>=11;
    scene.style.opacity=collapse>=1?'0':'1';
    scene.style.transform=`scaleY(${Math.max(.001,1-collapse)})`;
    scene.style.filter=closing?`brightness(${1+collapse*3})`:'none';
    $('.transition-flash').style.opacity=String(reduced?0:smooth((s-7.18)/.035)*(1-smooth((s-7.22)/.2))*.8);
    const line=$('.shutter-line');
    line.style.opacity=String(smooth((s-11.15)/.15)*(1-smooth((s-11.96)/.04)));
    line.style.transform=`scaleX(${1-pinch})`;

    intro.style.opacity=String(reduced?1-smooth((s-4.18)/.56):s<4.18?1:0);
    $('.identity').style.opacity=String(smooth((s-1.05)/.65));
    $('.identity').style.transform=`translate(-50%,-43%) scale(${.94+.06*smooth((s-1.05)/.65)})`;
    const beat=reduced?0:Math.pow((1-Math.cos(Math.max(0,s-1.05)*Math.PI*2/1.08))/2,2);
    $('.avatar-frame').style.transform=`translateY(${-4*beat}px) scale(${1+.025*beat})`;
    $('.pulse').style.opacity=String(1-smooth((s-1.15)/.5));
    document.querySelectorAll('.pulse i').forEach((ring,i)=>{
      const phase=(s*.75+i*.5)%1;
      ring.style.transform=`scale(${.25+phase*.9})`;ring.style.opacity=String((1-phase)*.8);
    });
    $('.dashed').style.transformOrigin='200px 200px';$('.dashed').style.transform=`rotate(${reduced?0:s*16}deg)`;
    $('.boot-title').style.clipPath=`inset(0 ${(1-clamp((s-1.9)/.7))*100}% 0 0)`;
    $('.boot-caption').style.opacity=String(smooth((s-2.5)/.35));
    $('.wave').style.transform=`scaleY(${reduced?1:.6+Math.sin(s*6)*.4})`;
    $('.boot-grid').style.opacity=String(smooth((s-.8)/.6)*(1-color));
    $('.boot-corners').style.opacity=String(smooth((s-.8)/.6)*(1-color));
    $('.boot-logs').style.opacity=String(1-smooth((s-3.7)/.45));
    document.querySelectorAll('.boot-logs div').forEach((line,i)=>line.style.opacity=String(smooth((s-.15-i*.38)/.3)));
    const assembling=clamp((s-5.08)/2.02);
    if(s>=4.18&&s<7.43)trace(reduced?1:assembling,s-4.18,reduced?1:dissolve);
    if(s>=4.74&&identityTexture)releaseIdentityTexture();
    canvas.style.opacity=String((reduced?smooth((s-4.18)/.56):s>=4.18?1:0)*(1-color));
    canvas.dataset.dissolve=dissolve.toFixed(3);
    art.style.opacity=String(fragments.length ? color : smooth((s-4.18)/2.9));
    // The art stays registered to the contours until the color transition finishes.
    art.style.transform=reduced?'none':`scale(${1+.03*smooth((s-7.42)/3.58)})`;
    $('.shade').style.opacity=String(color);
    hud.style.opacity=String(smooth((s-7.24)/.45));
    $('.sync-fill').style.transform=`scaleX(${p})`;
    sweep.style.opacity=reduced?'0':String(Math.sin(clamp((s-7.18)/.8)*Math.PI)*.1);
    sweep.style.transform=`translateX(${(-60+clamp((s-7.18)/.8)*120)}%)`;
    particles.forEach((node,i)=>{
      node.style.opacity=String(color*(.12+.28*(.5+.5*Math.sin(s*.9+i))));
      node.style.transform=`translate(${Math.sin(s*.4+i)*8}px,${-s*(2+i%3)}px)`;
    });
    subtitle.style.opacity=String(smooth((s-7.6)/.4)*(1-smooth((s-10.65)/.25)));
    if(Math.abs(t-lastUI)>60 || t===0 || t>=duration) {
      lastUI=t;
      $('#ratio').textContent=`${(p*100).toFixed(2).padStart(5,'0')}%`;
      $('.sync-phase').textContent=s<6.5?'INITIALIZING':p<1?'SYNCHRONIZING':'SYNC COMPLETE';
      $('#timecode').textContent=`00:${String(Math.floor(s)).padStart(2,'0')}:${String(Math.floor(s%1*30)).padStart(2,'0')}`;
      root.dataset.elapsed=s.toFixed(2);
      root.dataset.stage=s>=11?'shutter':s<1.15?'pulse':s<4.18?'identity':s<4.74?'dispersing':s<5.08?'scattered':s<7.18?'drawing':s<7.43?'colorize':'portrait';
    }
  }

  // The title card: identity over the artwork, no assembly, particles or HUD.
  // Calm (reduced motion) appears without a fade-in; short fades in gently.
  function prepareCard() {
    intro.style.opacity = '1';
    const identity = $('.identity');
    identity.style.opacity = '1';
    identity.style.transform = 'translate(-50%,-43%)';
    $('.boot-title').style.clipPath = 'none';
    $('.boot-caption').style.opacity = '1';
    $('.wave').style.transform = 'scaleY(1)';
    art.style.opacity = reduced ? '.28' : '.42';
    $('.shade').style.opacity = '.72';
    hud.style.opacity = '1';
  }
  function renderCard(t) {
    const s = t / 1000;
    const appear = reduced ? Math.min(1, s / .8) : smooth(s / .7);
    const leave = smooth((s - (cardDuration / 1000 - .55)) / .55);
    scene.style.opacity = String(appear * (1 - leave));
    root.dataset.elapsed = s.toFixed(2);
    root.dataset.stage = 'card';
  }
  function tick(now) {
    if (completed) return;
    const total = card ? cardDuration : skin ? skin.duration : duration;
    const elapsed = Math.min(total, (now - origin) * rate);
    if (card) renderCard(elapsed); else if (skin) skin.render(elapsed); else render(elapsed);
    if (elapsed >= total) {
      finish('complete');
      return;
    }
    frame = requestAnimationFrame(tick);
  }

  async function start() {
    await Promise.all([art.decode(), $('.avatar').decode()]);
    if (completed) return;
    if (!ctx) {
      finish('assetError');
      return;
    }
    prepareLineColor();
    fitTitle();
    if (card) {
      prepareCard();
      if (still) { renderCard(Math.min(cardDuration, still * 1000)); return; }
      renderCard(0);
      origin = performance.now();
      frame = requestAnimationFrame(tick);
      return;
    }
    if (skin) {
      skin.prepare();
      if (still) { skin.render(Math.min(skin.duration, still * 1000)); return; }
      skin.render(0);
      origin = performance.now();
      frame = requestAnimationFrame(tick);
      return;
    }
    paths = preparePaths(window.traceWallpaper ? window.traceWallpaper(art) : window.CONTOUR_PATHS || []);
    fragments = prepareFragments(paths);
    // A flat custom image can have no outlines. Its color layer fades in on its own.
    render(4179);
    if (fragments.length) {
      prepareIdentityTexture();
      drawIdentityPieces(0);
    }
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    lastTrace = -1;
    lastUI = -Infinity;
    if (still) {
      render(Math.min(duration, still * 1000));
      return;
    }
    render(0);
    origin = performance.now();
    frame = requestAnimationFrame(tick);
  }

  start().catch(() => finish('assetError'));
})();
