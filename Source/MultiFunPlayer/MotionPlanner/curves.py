import numpy as np
from numpy.polynomial import polynomial as P
from scipy.optimize import minimize,LinearConstraint

def prepare(raw):
    pts=sorted((float(x['at'])/1000,float(x['pos'])/100) for x in raw)
    if len(pts)<2 or any(not np.isfinite(t+y) or t<0 or not 0<=y<=1 for t,y in pts):raise ValueError('需要至少两个有效节点，时间非负，位置0到100')
    if any(a[0]==b[0] for a,b in zip(pts,pts[1:])):raise ValueError('脚本时间重复，请修正后载入')
    turns=[pts[0]];direction=0;extreme=departure=pts[0]
    for p in pts[1:]:
     if direction==0:
      if p[1]==pts[0][1]: departure=p
      if abs(p[1]-pts[0][1])>=.01-1e-12:
       if departure[0]>pts[0][0]:turns.append(departure)
       direction=np.sign(p[1]-pts[0][1]);extreme=p
     elif direction*(p[1]-extreme[1])>=0:extreme=p
     elif direction*(extreme[1]-p[1])>=.01-1e-12:
      turns.append(extreme);direction=-direction;extreme=p
    if direction!=0 and abs(extreme[1]-turns[-1][1])>=.01-1e-12:turns.append(extreme)
    if turns[-1][0]!=pts[-1][0]:turns.append(pts[-1])
    for i in range(1,len(pts)):
     if pts[i][1]!=pts[i-1][1]:continue
     if i==1 or pts[i-2][1]!=pts[i][1]:turns.append(pts[i-1])
     if i==len(pts)-1 or pts[i+1][1]!=pts[i][1]:turns.append(pts[i])
    turns=sorted(set(turns));filtered=[turns[0]]
    for p in turns[1:]:
     if p[0]-filtered[-1][0]>=.01-1e-10:filtered.append(p)
    if filtered[-1]!=turns[-1]:
     if len(filtered)>1:filtered.pop()
     filtered.append(turns[-1])
    t,y=np.array(filtered).T;dt=np.diff(t);dy=np.diff(y)
    acc=np.zeros(len(t))
    for i in range(1,len(t)-1):
     if dy[i-1]*dy[i]<0:acc[i]=np.sign(dy[i])*min(4*abs(dy[i-1])/dt[i-1]**2,4*abs(dy[i])/dt[i]**2)
    A=np.array([[1,1,1,1],[4,5,6,7],[12,20,30,42],[24,60,120,210]],float)
    def coeff(i,a):
     c=np.zeros(8);c[0]=y[i];c[2]=a[i]*dt[i]**2/2
     c[4:]=np.linalg.solve(A,[y[i+1]-c[0]-c[2],-2*c[2],a[i+1]*dt[i]**2-2*c[2],0])
     return c
    def extrema(c):
     roots=P.polyroots(P.polyder(c));u=[0.,1.]+[r.real for r in roots if abs(r.imag)<1e-8 and 0<r.real<1]
     return np.array(P.polyval(u,c))
    def metrics(c,h):return [float(max(abs(extrema(P.polyder(c,k)/h**k)))) for k in (1,2,3)]
    
    acc=np.trunc(acc*.999*1e6)/1e6
    return dict(pts=pts,t=t,y=y,dt=dt,dy=dy,acc=acc,coeff=coeff,metrics=metrics,extrema=extrema)

def block(s,lo,hi):
    t,y,dt,dy,coeff,metrics,extrema=[s[k] for k in ('t','y','dt','dy','coeff','metrics','extrema')]
    base=s['acc'];indices=range(lo,hi)
    if not any(base[lo+1:hi]):return base.copy(),False
    ids=[i for i in range(lo+1,hi) if base[i]!=0]
    caps=np.array([min(7.513189*abs(dy[i-1])/dt[i-1]**2,7.513189*abs(dy[i])/dt[i]**2) for i in ids]);signs=np.sign(base[ids]);scale=caps*signs
    x0=base[ids]/scale
    fixed=base.copy();fixed[ids]=0
    # Every segment's polynomial is affine in the shared accelerations.
    quad=[];rhs=[];constraint=[];lower=[];upper=[]
    g,w=np.polynomial.legendre.leggauss(8);g=(g+1)/2;w=w/2
    cache=[]
    for i in indices:
     c0=coeff(i,fixed);C=np.zeros((8,len(ids)))
     for k,j in enumerate(ids):
      if j in (i,i+1):
       b=fixed.copy();b[j]=scale[k];C[:,k]=coeff(i,b)-c0
     cache.append((i,c0,C))
     cb=coeff(i,base);ref=coeff(i,np.zeros(len(t)))
     energy=max(np.dot(w,P.polyval(g,P.polyder(cb,3))**2/dt[i]**6),1e-12)
     J=np.column_stack([P.polyval(g,P.polyder(C[:,k],3)) for k in range(len(ids))])/dt[i]**3
     q=P.polyval(g,P.polyder(c0,3))/dt[i]**3
     weight=np.sqrt(w/energy);quad.extend(J*weight[:,None]);rhs.extend(-q*weight)
     u=np.linspace(.001,.999,81)
     for order in (1,2,3):
      m=metrics(ref,dt[i])[order-1]
      if m<1e-9:continue
      H=np.column_stack([P.polyval(u,P.polyder(C[:,k],order)) for k in range(len(ids))])/dt[i]**order/m
      b=P.polyval(u,P.polyder(c0,order))/dt[i]**order/m
      low=np.full(len(u),-1.);up=np.ones(len(u))
      if order==1:
       if dy[i]>0:low[:]=0
       else:up[:]=0
      constraint.extend(H);lower.extend(low-b);upper.extend(up-b)
    Q=np.array(quad);v=np.array(rhs);H=Q.T@Q;linear=-Q.T@v
    result=minimize(lambda x:.5*x@H@x+linear@x,x0,jac=lambda x:H@x+linear,bounds=[(0,1)]*len(ids),constraints=[LinearConstraint(np.array(constraint),lower,upper)],method='SLSQP',options={'maxiter':300,'ftol':1e-10})
    if not result.success:return base.copy(),True
    candidate=base.copy();candidate[ids]=result.x*scale
    # Exact extrema check, independent of sampled optimizer constraints.
    def valid(a):
     for i in indices:
      c=coeff(i,a);v=extrema(P.polyder(c));ref=metrics(coeff(i,np.zeros(len(t))),dt[i])
      if dy[i] and min(v*np.sign(dy[i])) < -1e-7:return False
      if np.any(np.array(metrics(c,dt[i]))>np.array(ref)*1.00001+1e-6):return False
     return True
    mix=1.
    while not valid(candidate):
     mix*=.8;candidate=base+(candidate-base)*.8
     if mix<1e-7:return base.copy(),True
    return candidate,False

def optimize(s,progress=lambda p:None):
    a=s['acc'].copy();n=len(s['dt']);fallback=0
    for lo in range(0,n,24):
        hi=min(lo+24,n);b,failed=block(s,lo,hi);a[lo:hi+1]=b[lo:hi+1];fallback+=int(failed);progress(hi/n)
    return a,fallback

def coefficients(s,a):return [s['coeff'](i,a) for i in range(len(s['dt']))]

def view(s,co,start,end):
    ids=np.where((s['t'][:-1]<end)&(s['t'][1:]>start))[0];result=[]
    for i in ids:
        h=s['dt'][i];u=np.linspace(max(0,(start-s['t'][i])/h),min(1,(end-s['t'][i])/h),65)
        result.append(np.array([s['t'][i]+h*u]+[P.polyval(u,P.polyder(co[i],k))/h**k*100 for k in range(4)]).T)
    if not result:return []
    data=np.concatenate(result)
    if len(data)>18000:
        buckets=np.minimum(1499,((data[:,0]-start)/(end-start)*1500).astype(int));keep=set()
        for group in np.split(np.arange(len(data)),np.flatnonzero(np.diff(buckets))+1):
            keep.update([int(group[0]),int(group[-1])])
            for k in range(1,5):keep.update([int(group[np.argmin(data[group,k])]),int(group[np.argmax(data[group,k])])])
        data=data[sorted(keep)]
    return data.tolist()
