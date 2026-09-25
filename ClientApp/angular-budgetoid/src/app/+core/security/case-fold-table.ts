// Generated data. Do not hand-edit: every entry is a fact about Unicode, and a
// value corrected by eye here is a name that stops matching itself the day a
// second client folds it correctly.
//
// Derived from `CaseFolding.txt` of **Unicode 17.0**, statuses **C and F**
// only.
// Status S — the simple fold — is a different transform that leaves 'ß'
// unfolded, so a table built from it agrees with this one on almost every name
// and disagrees exactly where the difference decides a match.
//
// **The version is the product's choice and not the platform's, which is the
// whole reason this file exists.** `String.prototype.toLowerCase` is not a fold
// at all, and every platform API that is one reads whatever Unicode data the
// host shipped with: measured, the runtime this repository builds on reports
// Unicode 16.0, and 52 code points fold here that it does not fold at all.
// Two clients calling their platform would key one name to two values, which is
// a duplicate that never merges and a defect CON-009 forbids.
//
// The shape is two strings, base-36 throughout.
//
//   * `CASE_FOLD_RUNS` — comma-separated `start:count:delta` triples. Every
//     code point from `start` for `count` places folds to itself plus `delta`.
//     Runs
//     are what make this 6.6 KB rather than 20: folding is overwhelmingly a
//     constant shift over a contiguous block, and the alphabets where it is not
//     are the ones in the second string.
//   * `CASE_FOLD_MULTI` — comma-separated `from:to:to…` entries, for the
//     104
//     code points folding to more than one. 'ß' to 'ss' is the one everybody
//     knows;
//     the Armenian ligatures are the rest of the shape.
//
// `case-fold.ts` decodes both and is the only reader. The decoder lives there
// rather than here so the reviewable half is not buried under 691 runs.

export const CASE_FOLD_RUNS =
  '1t:q:w,51:1:lj,5c:n:w,60:7:w,74:1:1,76:1:1,78:1:1,7a:1:1,7c:1:1,7e:1' +
  ':1,7g:1:1,7i:1:1,7k:1:1,7m:1:1,7o:1:1,7q:1:1,7s:1:1,7u:1:1,7w:1:1,7y' +
  ':1:1,80:1:1,82:1:1,84:1:1,86:1:1,88:1:1,8a:1:1,8c:1:1,8e:1:1,8i:1:1,' +
  '8k:1:1,8m:1:1,8p:1:1,8r:1:1,8t:1:1,8v:1:1,8x:1:1,8z:1:1,91:1:1,93:1:' +
  '1,96:1:1,98:1:1,9a:1:1,9c:1:1,9e:1:1,9g:1:1,9i:1:1,9k:1:1,9m:1:1,9o:' +
  '1:1,9q:1:1,9s:1:1,9u:1:1,9w:1:1,9y:1:1,a0:1:1,a2:1:1,a4:1:1,a6:1:1,a' +
  '8:1:1,aa:1:1,ac:1:1,ae:1:1,ag:1:-3d,ah:1:1,aj:1:1,al:1:1,an:1:-7g,ap' +
  ':1:5u,aq:1:1,as:1:1,au:1:5q,av:1:1,ax:2:5p,az:1:1,b2:1:27,b3:1:5m,b4' +
  ':1:5n,b5:1:1,b7:1:5p,b8:1:5r,ba:1:5v,bb:1:5t,bc:1:1,bg:1:5v,bh:1:5x,' +
  'bj:1:5y,bk:1:1,bm:1:1,bo:1:1,bq:1:62,br:1:1,bt:1:62,bw:1:1,by:1:62,b' +
  'z:1:1,c1:2:61,c3:1:1,c5:1:1,c7:1:63,c8:1:1,cc:1:1,ck:1:2,cl:1:1,cn:1' +
  ':2,co:1:1,cq:1:2,cr:1:1,ct:1:1,cv:1:1,cx:1:1,cz:1:1,d1:1:1,d3:1:1,d5' +
  ':1:1,d7:1:1,da:1:1,dc:1:1,de:1:1,dg:1:1,di:1:1,dk:1:1,dm:1:1,do:1:1,' +
  'dq:1:1,dt:1:2,du:1:1,dw:1:1,dy:1:-2p,dz:1:-1k,e0:1:1,e2:1:1,e4:1:1,e' +
  '6:1:1,e8:1:1,ea:1:1,ec:1:1,ee:1:1,eg:1:1,ei:1:1,ek:1:1,em:1:1,eo:1:1' +
  ',eq:1:1,es:1:1,eu:1:1,ew:1:1,ey:1:1,f0:1:1,f2:1:1,f4:1:-3m,f6:1:1,f8' +
  ':1:1,fa:1:1,fc:1:1,fe:1:1,fg:1:1,fi:1:1,fk:1:1,fm:1:1,fu:1:8bv,fv:1:' +
  '1,fx:1:-4j,fy:1:8bs,g1:1:1,g3:1:-5f,g4:1:1x,g5:1:1z,g6:1:1,g8:1:1,ga' +
  ':1:1,gc:1:1,ge:1:1,n9:1:38,og:1:1,oi:1:1,om:1:1,ov:1:38,p2:1:12,p4:3' +
  ':11,p8:1:1s,pa:2:1r,pd:h:w,pv:9:w,qq:1:1,r3:1:8,r4:1:-u,r5:1:-p,r9:1' +
  ':-f,ra:1:-m,rc:1:1,re:1:1,rg:1:1,ri:1:1,rk:1:1,rm:1:1,ro:1:1,rq:1:1,' +
  'rs:1:1,ru:1:1,rw:1:1,ry:1:1,s0:1:-1i,s1:1:-1c,s4:1:-1o,s5:1:-1s,s7:1' +
  ':1,s9:1:-7,sa:1:1,sd:3:-3m,sg:g:28,sw:w:w,v4:1:1,v6:1:1,v8:1:1,va:1:' +
  '1,vc:1:1,ve:1:1,vg:1:1,vi:1:1,vk:1:1,vm:1:1,vo:1:1,vq:1:1,vs:1:1,vu:' +
  '1:1,vw:1:1,vy:1:1,w0:1:1,wa:1:1,wc:1:1,we:1:1,wg:1:1,wi:1:1,wk:1:1,w' +
  'm:1:1,wo:1:1,wq:1:1,ws:1:1,wu:1:1,ww:1:1,wy:1:1,x0:1:1,x2:1:1,x4:1:1' +
  ',x6:1:1,x8:1:1,xa:1:1,xc:1:1,xe:1:1,xg:1:1,xi:1:1,xk:1:1,xm:1:1,xo:1' +
  ':1,xq:1:1,xs:1:f,xt:1:1,xv:1:1,xx:1:1,xz:1:1,y1:1:1,y3:1:1,y5:1:1,y8' +
  ':1:1,ya:1:1,yc:1:1,ye:1:1,yg:1:1,yi:1:1,yk:1:1,ym:1:1,yo:1:1,yq:1:1,' +
  'ys:1:1,yu:1:1,yw:1:1,yy:1:1,z0:1:1,z2:1:1,z4:1:1,z6:1:1,z8:1:1,za:1:' +
  '1,zc:1:1,ze:1:1,zg:1:1,zi:1:1,zk:1:1,zm:1:1,zo:1:1,zq:1:1,zs:1:1,zu:' +
  '1:1,zw:1:1,zy:1:1,100:1:1,102:1:1,104:1:1,106:1:1,108:1:1,10a:1:1,10' +
  'c:1:1,10e:1:1,10g:1:1,10i:1:1,10k:1:1,10m:1:1,10o:1:1,10q:1:1,10s:1:' +
  '1,10u:1:1,10x:12:1c,3a8:12:5ls,3bb:1:5ls,3bh:1:5ls,3y0:6:-8,5mo:1:-4' +
  'su,5mp:1:-4st,5mq:1:-4sk,5mr:2:-4si,5mt:1:-4sj,5mu:1:-4sc,5mv:1:-4ro' +
  ',5mw:1:r7n,5mx:1:1,5n4:17:-2bk,5od:3:-2bk,5xc:1:1,5xe:1:1,5xg:1:1,5x' +
  'i:1:1,5xk:1:1,5xm:1:1,5xo:1:1,5xq:1:1,5xs:1:1,5xu:1:1,5xw:1:1,5xy:1:' +
  '1,5y0:1:1,5y2:1:1,5y4:1:1,5y6:1:1,5y8:1:1,5ya:1:1,5yc:1:1,5ye:1:1,5y' +
  'g:1:1,5yi:1:1,5yk:1:1,5ym:1:1,5yo:1:1,5yq:1:1,5ys:1:1,5yu:1:1,5yw:1:' +
  '1,5yy:1:1,5z0:1:1,5z2:1:1,5z4:1:1,5z6:1:1,5z8:1:1,5za:1:1,5zc:1:1,5z' +
  'e:1:1,5zg:1:1,5zi:1:1,5zk:1:1,5zm:1:1,5zo:1:1,5zq:1:1,5zs:1:1,5zu:1:' +
  '1,5zw:1:1,5zy:1:1,600:1:1,602:1:1,604:1:1,606:1:1,608:1:1,60a:1:1,60' +
  'c:1:1,60e:1:1,60g:1:1,60i:1:1,60k:1:1,60m:1:1,60o:1:1,60q:1:1,60s:1:' +
  '1,60u:1:1,60w:1:1,60y:1:1,610:1:1,612:1:1,614:1:1,616:1:1,618:1:1,61' +
  'a:1:1,61c:1:1,61e:1:1,61g:1:1,61n:1:-1m,61s:1:1,61u:1:1,61w:1:1,61y:' +
  '1:1,620:1:1,622:1:1,624:1:1,626:1:1,628:1:1,62a:1:1,62c:1:1,62e:1:1,' +
  '62g:1:1,62i:1:1,62k:1:1,62m:1:1,62o:1:1,62q:1:1,62s:1:1,62u:1:1,62w:' +
  '1:1,62y:1:1,630:1:1,632:1:1,634:1:1,636:1:1,638:1:1,63a:1:1,63c:1:1,' +
  '63e:1:1,63g:1:1,63i:1:1,63k:1:1,63m:1:1,63o:1:1,63q:1:1,63s:1:1,63u:' +
  '1:1,63w:1:1,63y:1:1,640:1:1,642:1:1,644:1:1,646:1:1,648:1:1,64a:1:1,' +
  '64c:1:1,64e:1:1,64o:8:-8,654:6:-8,65k:8:-8,660:8:-8,66g:6:-8,66x:1:-' +
  '8,66z:1:-8,671:1:-8,673:1:-8,67c:8:-8,69k:2:-8,69m:2:-22,69q:1:-5j9,' +
  '6a0:4:-2e,6ag:2:-8,6ai:2:-2s,6aw:2:-8,6ay:2:-34,6b0:1:-7,6bc:2:-3k,6' +
  'be:2:-3i,6jq:1:-5st,6ju:1:-6gv,6jv:1:-6di,6k2:1:s,6lc:g:g,6mb:1:1,79' +
  '2:q:q,8ow:1c:1c,8rk:1:1,8rm:1:-8af,8rn:1:-2xy,8ro:1:-89z,8rr:1:1,8rt' +
  ':1:1,8rv:1:1,8rx:1:-8bg,8ry:1:-8al,8rz:1:-8bj,8s0:1:-8bi,8s2:1:1,8s5' +
  ':1:1,8se:2:-8cf,8sg:1:1,8si:1:1,8sk:1:1,8sm:1:1,8so:1:1,8sq:1:1,8ss:' +
  '1:1,8su:1:1,8sw:1:1,8sy:1:1,8t0:1:1,8t2:1:1,8t4:1:1,8t6:1:1,8t8:1:1,' +
  '8ta:1:1,8tc:1:1,8te:1:1,8tg:1:1,8ti:1:1,8tk:1:1,8tm:1:1,8to:1:1,8tq:' +
  '1:1,8ts:1:1,8tu:1:1,8tw:1:1,8ty:1:1,8u0:1:1,8u2:1:1,8u4:1:1,8u6:1:1,' +
  '8u8:1:1,8ua:1:1,8uc:1:1,8ue:1:1,8ug:1:1,8ui:1:1,8uk:1:1,8um:1:1,8uo:' +
  '1:1,8uq:1:1,8us:1:1,8uu:1:1,8uw:1:1,8uy:1:1,8v0:1:1,8v2:1:1,8v4:1:1,' +
  '8v6:1:1,8vf:1:1,8vh:1:1,8vm:1:1,wu8:1:1,wua:1:1,wuc:1:1,wue:1:1,wug:' +
  '1:1,wui:1:1,wuk:1:1,wum:1:1,wuo:1:1,wuq:1:1,wus:1:1,wuu:1:1,wuw:1:1,' +
  'wuy:1:1,wv0:1:1,wv2:1:1,wv4:1:1,wv6:1:1,wv8:1:1,wva:1:1,wvc:1:1,wve:' +
  '1:1,wvg:1:1,ww0:1:1,ww2:1:1,ww4:1:1,ww6:1:1,ww8:1:1,wwa:1:1,wwc:1:1,' +
  'wwe:1:1,wwg:1:1,wwi:1:1,wwk:1:1,wwm:1:1,wwo:1:1,wwq:1:1,x0i:1:1,x0k:' +
  '1:1,x0m:1:1,x0o:1:1,x0q:1:1,x0s:1:1,x0u:1:1,x0y:1:1,x10:1:1,x12:1:1,' +
  'x14:1:1,x16:1:1,x18:1:1,x1a:1:1,x1c:1:1,x1e:1:1,x1g:1:1,x1i:1:1,x1k:' +
  '1:1,x1m:1:1,x1o:1:1,x1q:1:1,x1s:1:1,x1u:1:1,x1w:1:1,x1y:1:1,x20:1:1,' +
  'x22:1:1,x24:1:1,x26:1:1,x28:1:1,x2a:1:1,x2c:1:1,x2e:1:1,x2g:1:1,x2i:' +
  '1:1,x2k:1:1,x2m:1:1,x2x:1:1,x2z:1:1,x31:1:-r9g,x32:1:1,x34:1:1,x36:1' +
  ':1,x38:1:1,x3a:1:1,x3f:1:1,x3h:1:-wmg,x3k:1:1,x3m:1:1,x3q:1:1,x3s:1:' +
  '1,x3u:1:1,x3w:1:1,x3y:1:1,x40:1:1,x42:1:1,x44:1:1,x46:1:1,x48:1:1,x4' +
  'a:1:-wn8,x4b:1:-wnj,x4c:1:-wnf,x4d:1:-wn5,x4e:1:-wn8,x4g:1:-wlu,x4h:' +
  '1:-wmi,x4i:1:-wlx,x4j:1:ps,x4k:1:1,x4m:1:1,x4o:1:1,x4q:1:1,x4s:1:1,x' +
  '4u:1:1,x4w:1:1,x4y:1:1,x50:1:-1c,x51:1:-wn7,x52:1:-raw,x53:1:1,x55:1' +
  ':1,x57:1:-wo7,x58:1:1,x5a:1:1,x5c:1:1,x5e:1:1,x5g:1:1,x5i:1:1,x5k:1:' +
  '1,x5m:1:1,x5o:1:-wu9,x6d:1:1,xv4:28:-tzk,1ee9:q:w,1fcw:14:14,1fhs:10' +
  ':14,1fn4:b:13,1fng:f:13,1fnw:7:13,1fo4:2:13,1h1c:1f:1s,1h74:m:w,1jfk' +
  ':w:w,20cg:w:w,20f4:p:r,2olc:y:y';

export const CASE_FOLD_MULTI =
  '67:37:37,8g:2x:lj,95:jg:32,ds:2y:lo,pc:qh:lk:ld,q8:qt:lk:ld,13b:12d:' +
  '136,61i:2w:mp,61j:38:lk,61k:3b:lm,61l:3d:lm,61m:2p:ji,61q:37:37,66o:' +
  'qt:lv,66q:qt:lv:lc,66s:qt:lv:ld,66u:qt:lv:n6,680:64g:qh,681:64h:qh,6' +
  '82:64i:qh,683:64j:qh,684:64k:qh,685:64l:qh,686:64m:qh,687:64n:qh,688' +
  ':64g:qh,689:64h:qh,68a:64i:qh,68b:64j:qh,68c:64k:qh,68d:64l:qh,68e:6' +
  '4m:qh,68f:64n:qh,68g:65c:qh,68h:65d:qh,68i:65e:qh,68j:65f:qh,68k:65g' +
  ':qh,68l:65h:qh,68m:65i:qh,68n:65j:qh,68o:65c:qh,68p:65d:qh,68q:65e:q' +
  'h,68r:65f:qh,68s:65g:qh,68t:65h:qh,68u:65i:qh,68v:65j:qh,68w:674:qh,' +
  '68x:675:qh,68y:676:qh,68z:677:qh,690:678:qh,691:679:qh,692:67a:qh,69' +
  '3:67b:qh,694:674:qh,695:675:qh,696:676:qh,697:677:qh,698:678:qh,699:' +
  '679:qh,69a:67a:qh,69b:67b:qh,69e:67k:qh,69f:q9:qh,69g:q4:qh,69i:q9:n' +
  '6,69j:q9:n6:qh,69o:q9:qh,69u:67o:qh,69v:qf:qh,69w:q6:qh,69y:qf:n6,69' +
  'z:qf:n6:qh,6a4:qf:qh,6aa:qh:lk:lc,6ab:qh:lk:ld,6ae:qh:n6,6af:qh:lk:n' +
  '6,6aq:qt:lk:lc,6ar:qt:lk:ld,6as:qp:lv,6au:qt:n6,6av:qt:lk:n6,6b6:67w' +
  ':qh,6b7:qx:qh,6b8:r2:qh,6ba:qx:n6,6bb:qx:n6:qh,6bg:qx:qh,1dkw:2u:2u,' +
  '1dkx:2u:2x,1dky:2u:30,1dkz:2u:2u:2x,1dl0:2u:2u:30,1dl1:37:38,1dl2:37' +
  ':38,1dlf:12s:12u,1dlg:12s:12d,1dlh:12s:12j,1dli:132:12u,1dlj:12s:12l';
